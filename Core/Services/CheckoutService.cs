using CafePos.Core.Common;
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
    IComboService combos,
    ILogger<CheckoutService> logger) : ICheckoutService
{
    private const int MaxAttempts = 3;

    /// <summary>Payment is deferred unless the caller asks for it: the order starts unpaid.</summary>
    public Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment: null, OrderDetailsIntent.Default, cancellationToken);

    /// <inheritdoc />
    public Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent payment, CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment, OrderDetailsIntent.Default, cancellationToken);

    /// <inheritdoc />
    public Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, OrderDetailsIntent details, CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment: null, details, cancellationToken);

    /// <inheritdoc />
    public Task<Order> CheckoutAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent payment,
        OrderDetailsIntent details,
        CancellationToken cancellationToken = default) =>
        CheckoutCoreAsync(lines, payment, details, cancellationToken);

    private async Task<Order> CheckoutCoreAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent? payment,
        OrderDetailsIntent details,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0) throw new ValidationFailureException("Нельзя создать пустой заказ.");
        if (lines.Any(line => line.Quantity <= 0)) throw new ValidationFailureException("У каждой позиции заказа должно быть положительное количество.");

        /// <summary>The phone is validated BEFORE the loop and before any database work, so a typo in a phone number cannot consume an order number or leave a half-writte…</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

        var customerPhone = ResolveCustomerPhone(details);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CheckoutOnceAsync(lines, payment, details, customerPhone, cancellationToken);
            }
            catch (DbUpdateException exception) when (attempt < MaxAttempts && IsUniqueConstraintViolation(exception))
            {
                logger.LogWarning(exception, "Order number conflict on attempt {Attempt}; retrying", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>The phone that will actually be stored, or `null`.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private static string? ResolveCustomerPhone(OrderDetailsIntent details) =>
        ContactPhoneRule.ForStorage(details.CustomerPhone, details.OrderType);

    private async Task<Order> CheckoutOnceAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent? payment,
        OrderDetailsIntent details,
        string? customerPhone,
        CancellationToken cancellationToken)
    {
        /// <summary>The composition and the price the SERVER is willing to sell, rebuilt from the catalogue.</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

        var resolvedCompositions = await combos.ResolveSaleCompositionsAsync(lines, cancellationToken);
        var sellable = ReplaceComponents(lines, resolvedCompositions);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var shift = await RequireOpenShiftAsync(db, cancellationToken);

        /// <summary>Validate stock before anything is written, inside the same transaction.</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

        var demands = ComboExpander.Expand(sellable);
        var plan = await StockPlanner.BuildAsync(db, demands, cancellationToken);
        if (plan.HasShortages) throw new InsufficientStockException(plan.DescribeShortages());

        await WarnAboutMissingRecipesAsync(db, sellable, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OrderNumber = await OrderNumberAllocator.AllocateAsync(db, shift, cancellationToken),
            CreatedAt = now,
            Status = OrderStatus.InProgress,
            ShiftId = shift.Id,
            OrderType = details.OrderType,
            CustomerPhone = customerPhone,
            RequestedAt = details.RequestedAt
        };

        for (var index = 0; index < sellable.Count; index++)
        {
            var line = sellable[index];
            var composition = resolvedCompositions[index];

            /// <summary>THE ONE PRICE CHECK.</summary>
            /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

            var price = OrderLinePricing.Resolve(composition.Components, Money.ToKopecks(line.Price), composition.PriceKopecks);

            var item = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                PriceKopecks = price.PriceKopecks,
                ListPriceKopecks = price.ListPriceKopecks,
                Quantity = line.Quantity,
                SelectedModifierName = line.ModifierName,
                SelectedVariantName = line.VariantName
            };

            WriteComponents(db, item, composition.Components);
            order.Items.Add(item);
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

        /// <summary>Inside the transaction and before the single SaveChanges below: the payment row and Orders.PaidKopecks have to land together with the order, or the till records money against an order that does not exist.</summary>

        if (payment is not null)
            PaymentRecorder.Record(db, order, payment.Amount, payment.Method, now, logger);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Order #{Number} created: {Lines} lines, {Total} ₽", order.OrderNumber, order.Items.Count, order.TotalPrice);
        return order;
    }

    /// <summary>The same lines with their composition replaced by the resolved one.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private static IReadOnlyList<CheckoutLine> ReplaceComponents(
        IReadOnlyList<CheckoutLine> lines,
        IReadOnlyList<SaleComposition> resolved)
    {
        var sellable = new List<CheckoutLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
            sellable.Add(lines[index] with { Components = resolved[index].Components });
        return sellable;
    }

    /// <summary>Writes the composition snapshot onto the line.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private static void WriteComponents(AppDbContext db, OrderItem item, IReadOnlyList<CheckoutComponent> components)
    {
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            db.OrderItemComponents.Add(new OrderItemComponent
            {
                Id = Guid.NewGuid(),
                OrderItemId = item.Id,
                ProductId = component.ProductId,
                ProductName = component.ProductName,
                QuantityPerUnit = component.QuantityPerUnit,
                UnitPriceKopecks = component.UnitPriceKopecks,
                ReferencePriceKopecks = component.ReferencePriceKopecks,
                SortOrder = index
            });
        }
    }

    /// <summary>Warns about bundle components that have no recipe, and sells anyway.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private async Task WarnAboutMissingRecipesAsync(AppDbContext db, IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken)
    {
        var componentIds = lines
            .Where(line => line.Components is { Count: > 0 })
            .SelectMany(line => line.Components!)
            .Select(component => component.ProductId)
            .Distinct()
            .ToList();

        if (componentIds.Count == 0) return;

        var withRecipe = await db.RecipeItems.AsNoTracking()
            .Where(item => componentIds.Contains(item.ProductId))
            .Select(item => item.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var withoutRecipe = ComboExpander.DescribeComponentsWithoutRecipe(lines, withRecipe.ToHashSet());
        foreach (var message in withoutRecipe)
        {
            logger.LogWarning("Bundle component without a recipe: {Detail}", message);
        }
    }

    /// <summary>The open shift, or a refusal naming the fix.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private async Task<Shift> RequireOpenShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(current => current.IsActive, cancellationToken);
        return shift ?? throw new ConflictException(
            "Смена не открыта. Откройте смену и внесите размен, чтобы можно было продавать.");
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}
