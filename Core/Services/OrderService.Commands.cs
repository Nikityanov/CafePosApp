using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Status transitions, cancellation and item editing of an order.</summary>
public sealed partial class OrderService
{
    /// <summary>Orders.CancellationReason and OrderStatusHistory.Comment are both 300 characters.</summary>
    private const int MaxCancellationReasonLength = 300;

    public async Task<Order> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Completed)
            throw new ConflictException("Этот заказ уже закрыт.");

        /// <summary>Both transitions are gated, so this sits before the switch: nothing may leave the bar unpaid, which is the only thing that makes "revenue counts compl…</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (!order.IsFullyPaid) throw new ConflictException("Заказ не оплачен: сначала примите оплату.");

        var now = timeProvider.GetUtcNow();
        if (order.Status == OrderStatus.InProgress)
        {
            order.Status = OrderStatus.Ready;
            order.ReadyAt ??= now;
        }
        else
        {
            order.Status = OrderStatus.Completed;
            order.CompletedAt ??= now;
        }

        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            Status = order.Status,
            ChangedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderNumber} moved to {Status}", order.OrderNumber, order.Status);
        return order;
    }

    /// <remarks>`docs/decisions/orders.md`</remarks>

    public async Task MarkSeenAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var changed = await db.Orders
            .Where(order => order.Id == orderId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.SeenAt, now), cancellationToken)
            .ConfigureAwait(false);

        // Information, not Debug: the file provider's floor is Information, so a Debug line here is
        // invisible and the next person to suspect this write has nothing to read.
        logger.LogInformation("Order {OrderId} marked seen at {SeenAt} ({Changed} row)", orderId, now, changed);
    }

    /// <inheritdoc />
    public async Task<Order> AddContactDetailsAsync(
        Guid orderId,
        string? phone,
        DateTimeOffset? requestedAt,
        bool promoteToTakeaway = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        /// <summary>A voided order is a sale that did not happen.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Заказ отменён — дописывать к нему нечего.");

        var wantsPhone = !string.IsNullOrWhiteSpace(phone);

        /// <summary>THE GATE, AND IT IS DELIBERATELY HERE RATHER THAN IN THE SHEET.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (wantsPhone && order.OrderType != OrderType.Takeaway && !promoteToTakeaway)
            throw new ValidationFailureException(
                "Телефон сохраняется только для заказа «С собой». Смените тип выдачи, чтобы записать номер.");

        if (wantsPhone)
        {
            // Validated as Takeaway in both branches, because that is the mode the number is being stored under — including the branch where it already was one.
            // Почему так — `docs/decisions/orders.md`

            order.CustomerPhone = ContactPhoneRule.ForStorage(phone, OrderType.Takeaway);

            // The promotion, and it happens WITH the number rather than separately: a takeaway order with
            // no phone is a perfectly normal order, so the type must not change when no number was named.
            order.OrderType = OrderType.Takeaway;
        }

        if (requestedAt is not null) order.RequestedAt = requestedAt;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Order {OrderId} annotated: phone {HasPhone}, promised {HasPromise}, type {OrderType}",
            orderId, order.CustomerPhone is not null, order.RequestedAt is not null, order.OrderType);

        return order;
    }

    /// <summary>Voids a sale: the order leaves the revenue, the money goes back, and — only if the operator says nothing was made — the stock write-off is undone.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task CancelOrderAsync(
        Guid orderId,
        string? reason = null,
        StockDisposition stock = StockDisposition.LeaveWrittenOff,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Same shape as CheckoutService: the refund rows, the stock movements and the status change are one fact about the till, and a partial write of it is th…
        // Почему так — `docs/decisions/orders.md`

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        /// <summary>The only guard left. "Already cancelled" is what makes cancellation single-shot: a second tap must not refund the money twice, and it is also the only state in which the money has provably already left the drawer.</summary>

        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Заказ уже отменён.");

        var now = timeProvider.GetUtcNow();
        var operatorReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        /// <summary>A paid order CAN be cancelled — it used to be refused, which was the only thing keeping the sale in the books while the cash stayed in the drawer.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var refundedKopecks = order.PaidKopecks;
        if (refundedKopecks > 0)
            PaymentRecorder.Refund(db, order, refundedKopecks, $"Отмена заказа{(operatorReason is null ? "" : $": {operatorReason}")}", now, logger, allowUncompletedOrder: true);

        IReadOnlyList<string> irreversible = [];
        if (stock == StockDisposition.ReturnToStock)
        {
            var reversal = await StockPlan.ReverseAsync(db, order, now, logger, cancellationToken);
            irreversible = reversal.IrreversibleIngredients;
        }

        var cancellationReason = ComposeCancellationReason(operatorReason, refundedKopecks, stock, irreversible);
        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = now;
        order.CancellationReason = cancellationReason;

        /// <summary>One history row carrying the whole story, not just the reason: the refund and the stock disposition are the parts an auditor asks about, and the status field alone would show a void without saying where the money went.</summary>

        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            Status = OrderStatus.Cancelled,
            ChangedAt = now,
            Comment = cancellationReason
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderNumber} cancelled: {Refunded} ₽ refunded, stock {Stock} ({Reason})",
            order.OrderNumber, Money.FromKopecks(refundedKopecks), stock, cancellationReason);
    }

    /// <summary>Orders.CancellationReason and OrderStatusHistory.Comment are both 300 characters (AppDbContext.Catalog), so the composition is built once and truncate…</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    private static string ComposeCancellationReason(
        string? operatorReason,
        long refundedKopecks,
        StockDisposition stock,
        IReadOnlyList<string> irreversible)
    {
        var parts = new List<string>(4);
        if (operatorReason is not null) parts.Add(operatorReason);
        if (refundedKopecks > 0) parts.Add($"возвращено {TextFormat.Money(Money.FromKopecks(refundedKopecks))}");
        if (stock == StockDisposition.ReturnToStock)
        {
            if (irreversible.Count == 0)
            {
                parts.Add("остатки возвращены на склад");
            }
            else
            {
                /// <summary>Names when the catalogue still has them, identifiers when it does not.</summary>
                /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

                var names = string.Join(", ", irreversible);
                parts.Add($"вернуто частично: {names} удал{(irreversible.Count == 1 ? "ён" : "ены")} со склада");
            }
        }
        else
        {
            parts.Add("остатки списаны");
        }

        var text = string.Join("; ", parts);
        return text.Length <= MaxCancellationReasonLength ? text : text[..MaxCancellationReasonLength];
    }

    /// <summary>Differential update: existing lines keep their identifiers, only quantities are updated, missing lines are removed and new lines are inserted.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) throw new ValidationFailureException("Нельзя сохранить пустой заказ.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Same shape as checkout and cancellation: the lines and the stock correction are one fact
        // about the till, so a shortage or a refusal has to leave the order exactly as it was.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var order = await db.Orders
            .Include(current => current.Items)
                .ThenInclude(item => item.Components)
            .FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");
        if (order.Status != OrderStatus.InProgress)
            throw new ConflictException("Изменять можно только заказ, который еще готовится.");

        await ApplyStockForEditAsync(db, order, items, cancellationToken);

        /// <summary>The reconciled line set, kept separately from order.Items: a new line is tracked through the DbSet (an entity pushed into the Items collection of a tr…</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var resulting = new List<OrderItem>(items.Count);

        foreach (var incoming in items)
        {
            bool Matches(OrderItem candidate) =>
                OrderLineKey.For(
                    candidate.ProductId,
                    candidate.SelectedModifierName,
                    candidate.SelectedVariantName,
                    ToComponentSignature(candidate))
                == OrderLineKey.For(
                    incoming.ProductId,
                    incoming.SelectedModifierName,
                    incoming.SelectedVariantName,
                    ToComponentSignature(incoming));

            /// <summary>BOTH LISTS, AND THE ORDER IS THE POINT — this lookup was the site of two defects in a row, and both are the same mistake about which lines exist.</summary>
            /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

            var existing = resulting.FirstOrDefault(Matches) ?? order.Items.FirstOrDefault(Matches);

            if (existing is null)
            {
                /// <summary>A line ADDED to an open order gets its allowed price decided here, exactly as at checkout, so the shift report's discount section cannot be walked aro…</summary>
                /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

                var price = OrderLinePricing.Resolve(
                    ToCheckoutComponents(incoming),
                    incoming.PriceKopecks,
                    await ResolveBundlePriceAsync(incoming, cancellationToken));

                var added = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    ProductId = incoming.ProductId,
                    ProductName = incoming.ProductName,
                    PriceKopecks = price.PriceKopecks,
                    ListPriceKopecks = price.ListPriceKopecks,
                    Quantity = incoming.Quantity,
                    SelectedModifierName = incoming.SelectedModifierName,
                    SelectedVariantName = incoming.SelectedVariantName
                };
                db.OrderItems.Add(added);
                WriteComponents(db, added, incoming);
                resulting.Add(added);
            }
            else
            {
                existing.Quantity = incoming.Quantity;
                existing.Price = incoming.Price;

                /// <summary>ListPriceKopecks is deliberately NOT rewritten here.</summary>
                /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

                SyncComponents(db, existing, incoming);
                resulting.Add(existing);
            }
        }

        var removed = order.Items.Where(item => resulting.All(keep => keep.Id != item.Id)).ToList();
        foreach (var item in removed) order.Items.Remove(item);
        if (removed.Count > 0) db.OrderItems.RemoveRange(removed);

        order.RecalculateTotal(resulting);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Order {OrderNumber} updated: {Items} lines, total {Total}", order.OrderNumber, resulting.Count, order.TotalPrice);
    }

    /// <summary>
    /// The stock half of an edit: what the order took before, what it should hold now, and the
    /// difference between the two, which is the only movement an edit may write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision itself is <see cref="StockDeltaPlan"/> — a pure type, unit-testable without a
    /// database. This is the part that reads: the journal, both recipe passes and the shelf.
    /// </para>
    /// <para>
    /// <b>WHY THE JOURNAL, AND NOT THE STORED LINES.</b> The lines say what the order now is; the
    /// journal says what actually left the shelf. Once an edit moves stock, the two are different
    /// facts and only the second one can be subtracted — and the cancellation reads the same
    /// journal, so an edit that ignored it would make a later cancellation return the wrong amount.
    /// </para>
    /// </remarks>

    private async Task ApplyStockForEditAsync(
        AppDbContext db,
        Order order,
        IReadOnlyCollection<OrderItem> incoming,
        CancellationToken cancellationToken)
    {
        var journal = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.OrderId == order.Id)
            .ToListAsync(cancellationToken);

        // Before anything is computed: a receipt booked against the order makes "what this order
        // took" unanswerable, and a number nobody can justify is worse than a refusal.
        StockPlan.EnsureJournalDescribesTheOrder(journal, order.OrderNumber);

        var before = await StockPlanner.BuildAsync(db, ComboExpander.Expand(order.Items.Select(ToCheckoutLine)), cancellationToken);
        var after = await StockPlanner.BuildAsync(db, ComboExpander.Expand(incoming.Select(ToCheckoutLine)), cancellationToken);

        var consumed = journal
            .GroupBy(movement => movement.IngredientId)
            .ToDictionary(group => group.Key, group => -group.Sum(movement => movement.QuantityDelta));

        var touched = before.Required.Keys
            .Concat(after.Required.Keys)
            .Concat(consumed.Keys)
            .Distinct()
            .ToList();

        var ingredients = await db.Ingredients
            .Where(ingredient => touched.Contains(ingredient.Id))
            .ToListAsync(cancellationToken);

        // An ingredient in the journal whose row is gone cannot receive anything back, and skipping
        // it quietly would leave the shelf wrong with nothing on screen to say so.
        var gone = touched.Where(id => ingredients.All(ingredient => ingredient.Id != id)).ToList();
        if (gone.Count > 0)
            throw new ConflictException(
                $"Ингредиент по заказу #{order.OrderNumber} удалён из каталога, остатки не изменены: {string.Join(", ", gone)}");

        var positions = ingredients
            .Select(ingredient => new StockPosition(
                ingredient.Id,
                ingredient.Name,
                ingredient.Unit,
                ingredient.StockQuantity,
                consumed.GetValueOrDefault(ingredient.Id),
                before.Required.GetValueOrDefault(ingredient.Id),
                after.Required.GetValueOrDefault(ingredient.Id)))
            .ToList();

        var plan = StockDeltaPlan.Decide(positions);

        if (plan.RecipeDrift.Count > 0)
            throw new ConflictException(
                "Рецепт изменился после оформления заказа, списание не пересчитать: "
                + string.Join("; ", plan.RecipeDrift.Select(drift => drift.Describe())));

        if (plan.Shortages.Count > 0)
            throw new InsufficientStockException(plan.DescribeShortages());

        if (plan.IsEmpty) return;

        var now = timeProvider.GetUtcNow();
        db.StockMovements.AddRange(StockPlan.WriteOffDeltas(plan, ingredients, order, now, logger));
    }

    /// <summary>A stored order line in the shape the stock planner reads.</summary>

    private static CheckoutLine ToCheckoutLine(OrderItem item) =>
        new(
            item.ProductId,
            item.ProductName,
            item.Price,
            item.Quantity,
            item.SelectedModifierName,
            item.SelectedVariantName,
            ToCheckoutComponents(item));

    /// <summary>The bundle's own price from the catalogue, or `null` when the line is not a live bundle.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    private async Task<long?> ResolveBundlePriceAsync(OrderItem incoming, CancellationToken cancellationToken)
    {
        if (incoming.Components.Count == 0) return null;

        var probe = new CheckoutLine(
            incoming.ProductId,
            incoming.ProductName,
            incoming.Price,
            incoming.Quantity,
            incoming.SelectedModifierName,
            incoming.SelectedVariantName,
            ToCheckoutComponents(incoming));

        var prices = await combos.ResolveSalePricesAsync([probe], cancellationToken);
        return prices.Count == 0 ? null : prices[0];
    }

    /// <summary>The (dish, count per unit) pairs a line's composition folds into its merge key.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    private static IEnumerable<(Guid productId, int quantity)> ToComponentSignature(OrderItem item) =>
        item.Components.Select(component => (component.ProductId, component.QuantityPerUnit));

    /// <summary>The composition in the shape `OrderLinePricing` and `ComboPricing` read.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    private static IReadOnlyList<CheckoutComponent> ToCheckoutComponents(OrderItem item) =>
        item.Components
            .Select(component => new CheckoutComponent(
                component.ProductId,
                component.ProductName,
                component.QuantityPerUnit,
                component.UnitPriceKopecks,
                component.ReferencePriceKopecks))
            .ToList();

    /// <summary>The composition snapshot for a line, written through the DbSet.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    private static void WriteComponents(AppDbContext db, OrderItem item, OrderItem incoming)
    {
        for (var index = 0; index < incoming.Components.Count; index++)
        {
            var component = incoming.Components[index];
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

    /// <summary>Replaces the composition of a line that already exists: what the caller sent is what the sale is now, so the previous snapshot rows go and the current ones take their place.</summary>

    private static void SyncComponents(AppDbContext db, OrderItem existing, OrderItem incoming)
    {
        if (existing.Components.Count == 0 && incoming.Components.Count == 0) return;

        foreach (var stale in existing.Components.ToList())
        {
            /// <summary>Removed through the DbSet as well: the rows are tracked entities of the loaded composition, and detaching them from the collection alone would leave t…</summary>
            /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

            db.OrderItemComponents.Remove(stale);
            existing.Components.Remove(stale);
        }

        WriteComponents(db, existing, incoming);
    }
}
