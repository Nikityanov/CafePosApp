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

        // The phone is validated BEFORE the loop and before any database work, so a typo in a phone
        // number cannot consume an order number or leave a half-written order behind. It is validated
        // rather than silently dropped because a takeaway customer who gave a number wants to be
        // reachable, and a number nobody can dial is worse than no number at all — the kitchen would
        // try to call it.
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

    /// <summary>
    /// The phone that will actually be stored, or <c>null</c>.
    /// <para>
    /// <b>COUNTER SERVICE STORES NONE, AND THAT IS THE POINT OF THE WHOLE FEATURE.</b> 152-ФЗ
    /// ст. 6(1)(5) permits a phone to be processed only where it is needed to perform the contract, and
    /// ст. 5(7) requires erasing it once that purpose is met; a customer eating on the premises needs
    /// to be found in no way at all, so the value is dropped HERE, at the boundary, instead of being
    /// written and then deleted later. EDPB Guidelines 2/2019 п. 25 is the same argument from the other
    /// side: where a less intrusive option exists, the processing is unnecessary. CoAP 13.11 ч.12
    /// prices unlawful disclosure at 3–5 million ₽ for 1 000–10 000 subjects and counts ROWS, so what
    /// matters is how many rows exist, not whether the column does.
    /// <para>
    /// The three decisions themselves live in <see cref="ContactPhoneRule"/>, not here. That is not
    /// tidiness: this method used to be the only place the rule existed, which meant the second door into
    /// the same column — a phone added to an order AFTER it was paid for — had to copy it. A copy of a
    /// legal contour is exactly the kind of thing nobody writes a test for and that quietly diverges. The
    /// reasoning stays here; the mechanism does not.
    /// </para>
    /// </summary>
    private static string? ResolveCustomerPhone(OrderDetailsIntent details) =>
        ContactPhoneRule.ForStorage(details.CustomerPhone, details.OrderType);

    private async Task<Order> CheckoutOnceAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent? payment,
        OrderDetailsIntent details,
        string? customerPhone,
        CancellationToken cancellationToken)
    {
        // The composition and the price the SERVER is willing to sell, rebuilt from the catalogue.
        // Everything from here on uses these rows and not the ones the client sent: the names on the
        // receipt, the ingredients written off and the price the control compares against all have to
        // be the catalogue's figures, or a forged cart decides them.
        var resolvedCompositions = await combos.ResolveSaleCompositionsAsync(lines, cancellationToken);
        var sellable = ReplaceComponents(lines, resolvedCompositions);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var shift = await RequireOpenShiftAsync(db, cancellationToken);

        // Validate stock before anything is written, inside the same transaction. The demands are the
        // EXPANDED ones, so a bundle's slots reach the recipe lookup instead of the bundle line (which
        // has no recipe of its own) and its ingredients are written off for the first time.
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

            // THE ONE PRICE CHECK. The allowed price is the bundle's OWN price, read from the
            // catalogue — not a sum of its parts, and not anything the client asked for. The charged
            // price stays what the caller sent, because a manual review at the till is allowed: it is
            // the divergence that gets reported afterwards, not something the till has to argue about.
            // For an ordinary dish the allowed price is the charged one, as it always was.
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
    /// The same lines with their composition replaced by the resolved one. <c>with</c> on a record, so
    /// the line keeps its identity, price and quantity — only the slots are swapped.
    /// <para>
    /// The bundle's own price does NOT travel back into the line either: it is the allowed side of the
    /// comparison in <see cref="OrderLinePricing.Resolve"/>, and a line's <c>Price</c> is what the
    /// customer was charged, including a manual review the till is allowed to make.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CheckoutLine> ReplaceComponents(
        IReadOnlyList<CheckoutLine> lines,
        IReadOnlyList<SaleComposition> resolved)
    {
        var sellable = new List<CheckoutLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
            sellable.Add(lines[index] with { Components = resolved[index].Components });
        return sellable;
    }

    /// <summary>
    /// Writes the composition snapshot onto the line.
    /// <para>
    /// <c>OrderItemComponent.ProductId</c> is a plain value with no foreign key, so a line outlives the
    /// catalogue entry that produced it — which is what lets an old receipt be reprinted unchanged and
    /// lets a deleted dish not take a historical order with it.
    /// </para>
    /// <para>
    /// Through the DbSet, and NOT pushed onto <c>item.Components</c> by hand. This is the trap written
    /// out at <c>DraftOrderService</c> and <c>CatalogService.SyncVariants</c> — an entity that was never
    /// Added is tracked as Modified against an unchanged parent and EF then UPDATEs a row that does not
    /// exist — plus its mirror image, which cost a day of confusion: once the parent IS tracked, the
    /// Add fixes up the inverse navigation by itself, so pushing the same instance in again leaves the
    /// collection holding every snapshot twice. The order handed back to the caller still carries its
    /// composition, because the fixup is what links it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Warns about bundle components that have no recipe, and sells anyway.
    /// <para>
    /// A warning, never a refusal: a missing recipe row is a catalogue omission, and blocking the sale
    /// would turn it into a till that cannot sell coffee until somebody fixes the catalogue. But
    /// SILENCE is the actual damage — the ingredients leave the shelf without being written off, and
    /// the stock quietly drifts away from the books with nothing on screen to show it. Toast keeps an
    /// 86 report for the same reason: the sale happens, and the missing link is visible.
    /// </para>
    /// <para>
    /// Read on the SAME context and inside the same transaction as the shortage check, for one reason:
    /// a second connection reading while this transaction is open is a lock the phone's flash does not
    /// need to take, and the answer cannot differ between two connections anyway — the catalogue is not
    /// being written by the sale.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The open shift, or a refusal naming the fix.
    /// </summary>
    /// <remarks>
    /// This used to CREATE the shift, so that selling could never fail for want of one. That is now
    /// a refusal, and the change is the point of the opening screen: a shift created by the first
    /// sale is a shift whose opening change nobody ever recorded, and the end-of-shift count is then
    /// compared against a drawer that was never declared to hold anything.
    /// <para>
    /// The refusal is thrown from inside the checkout transaction and before stock is validated, so the
    /// transaction is unwound untouched and no partial order is left behind.
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
