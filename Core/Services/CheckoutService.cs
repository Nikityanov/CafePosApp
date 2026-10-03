using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed class CheckoutService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<CheckoutService> logger) : ICheckoutService
{
    private const int MaxAttempts = 3;

    /// <summary>Payment is deferred unless the caller asks for it: the order starts unpaid.</summary>
    public Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment: null, cancellationToken);

    /// <inheritdoc />
    public Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent payment, CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment, cancellationToken);

    private async Task<Order> CheckoutCoreAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent? payment, CancellationToken cancellationToken)
    {
        if (lines.Count == 0) throw new ValidationFailureException("Нельзя создать пустой заказ.");
        if (lines.Any(line => line.Quantity <= 0)) throw new ValidationFailureException("У каждой позиции заказа должно быть положительное количество.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CheckoutOnceAsync(lines, payment, cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt < MaxAttempts && IsUniqueConstraintViolation(exception))
            {
                logger.LogWarning(exception, "Order number conflict on attempt {Attempt}; retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<Order> CheckoutOnceAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent? payment, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var shift = await RequireOpenShiftAsync(db, cancellationToken);

        // Validate stock before anything is written, inside the same transaction.
        var plan = await StockPlanner.BuildAsync(db, lines.Select(line => (line.ProductId, line.Quantity)).ToList(), cancellationToken);
        if (plan.HasShortages) throw new InsufficientStockException(plan.DescribeShortages());

        var now = timeProvider.GetUtcNow();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = await OrderNumberAllocator.AllocateAsync(db, shift, cancellationToken),
            CreatedAt = now,
            Status = OrderStatus.InProgress,
            ShiftId = shift.Id
        };

        foreach (var line in lines)
        {
            order.Items.Add(new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                Price = line.Price,
                Quantity = line.Quantity,
                SelectedModifierName = line.ModifierName,
                SelectedVariantName = line.VariantName
            });
        }

        order.RecalculateTotal();

        db.Orders.Add(order);
        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            Status = OrderStatus.InProgress,
            ChangedAt = now
        });
        db.StockMovements.AddRange(plan.WriteOff(order, now, logger));

        // Inside the transaction and before the single SaveChanges below: the payment row and
        // Orders.PaidKopecks have to land together with the order, or the till records money
        // against an order that does not exist.
        if (payment is not null)
            PaymentRecorder.Record(db, order, payment.Amount, payment.Method, now, logger);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Order #{Number} created: {Lines} lines, {Total} ₽", order.OrderNumber, order.Items.Count, order.TotalPrice);
        return order;
    }

    /// <summary>
    /// The open shift, or a refusal naming the fix.
    /// </summary>
    /// <remarks>
    /// This used to CREATE the shift, so that selling could never fail for want of one. That is now
    /// a refusal, and the change is the point of the opening screen: a shift created by the first
    /// sale is a shift whose opening change nobody ever recorded, and the end-of-shift count is then
    /// compared against a drawer that was never declared to hold anything.
    /// <para>
    /// The refusal is thrown from inside the checkout transaction and before stock is validated, so
    /// the transaction is unwound untouched and no partial order is left behind.
    /// </para>
    /// </remarks>
    private async Task<Shift> RequireOpenShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(current => current.IsActive, cancellationToken);
        return shift ?? throw new ConflictException(
            "Смена не открыта. Откройте смену и внесите размен, чтобы можно было продавать.");
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
