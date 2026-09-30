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

    public async Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0) throw new ValidationFailureException("Нельзя создать пустой заказ.");
        if (lines.Any(line => line.Quantity <= 0)) throw new ValidationFailureException("У каждой позиции заказа должно быть положительное количество.");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CheckoutOnceAsync(lines, cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt < MaxAttempts && IsUniqueConstraintViolation(exception))
            {
                logger.LogWarning(exception, "Order number conflict on attempt {Attempt}; retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task<Order> CheckoutOnceAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var shift = await EnsureActiveShiftAsync(db, cancellationToken);

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

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Order #{Number} created: {Lines} lines, {Total} ₽", order.OrderNumber, order.Items.Count, order.TotalPrice);
        return order;
    }

    private async Task<Shift> EnsureActiveShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(current => current.IsActive, cancellationToken);
        if (shift is not null) return shift;

        shift = new Shift
        {
            Id = Guid.NewGuid(),
            StartTime = timeProvider.GetUtcNow(),
            IsActive = true,
            NextOrderNumber = 1
        };
        db.Shifts.Add(shift);
        await db.SaveChangesAsync(cancellationToken);
        return shift;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
