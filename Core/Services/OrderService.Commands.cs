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

        // Both transitions are gated, so this sits before the switch: nothing may leave the bar
        // unpaid, which is the only thing that makes "revenue counts completed orders" and "the
        // till received the money" the same statement.
        //
        // WHY checking IsFullyPaid here is sound — and the condition that would break it:
        // UpdateOrderAsync refuses any order that is not InProgress, so the total is FROZEN the
        // moment an order becomes Ready. An order that was fully paid can therefore never become
        // underpaid, because the only writable total cannot change afterwards. A refund lowers
        // PaidKopecks, so it would break exactly this — which is why RefundAsync accepts only
        // Completed orders: by then the status cannot move and the guard can never be reached again
        // with a lowered scalar. "Paid" stays permanent for every order that is still travelling
        // through the bar, and a refund is something that happens to a sale that already left it.
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

    /// <inheritdoc />
    /// <remarks>
    /// An explicit scalar update, not a tracked load and SaveChanges. This runs from a card tap on a
    /// board that rebuilds itself every few seconds, and it touches exactly one column; loading the
    /// entity would drag in every navigation and invite an accidental write to something else.
    /// <para>
    /// THE PREDICATE IS THE KEY AND NOTHING ELSE, and it is there because the first version was an
    /// over-engineering that broke the feature. It read
    /// <c>Id == orderId &amp;&amp; (SeenAt == null || SeenAt &lt; now)</c> — "not already seen" as a
    /// database-side guard — and EF could not translate the disjunction over a nullable
    /// <c>DateTimeOffset</c>, so <c>ExecuteUpdateAsync</c> threw
    /// <c>InvalidOperationException: The LINQ expression could not be translated</c> on the FIRST tap.
    /// The dot therefore never cleared: the write was attempted, failed, and was swallowed by the
    /// caller's catch into a file log nobody reads. The guard was redundant in the first place —
    /// <c>now</c> is later than any earlier <c>SeenAt</c>, so writing it unconditionally is ALREADY
    /// monotonic, and the caller only calls this when the order is genuinely unseen. A guard that
    /// cannot be translated is worse than no guard.
    /// </para>
    /// </remarks>
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

        // A voided order is a sale that did not happen. Its total is gone and its money went back, so a
        // customer who "remembers a number" for it is describing a different order — and a phone on a
        // cancelled row would outlive the sale it describes.
        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Заказ отменён — дописывать к нему нечего.");

        var wantsPhone = !string.IsNullOrWhiteSpace(phone);

        // THE GATE, AND IT IS DELIBERATELY HERE RATHER THAN IN THE SHEET. Counter service may not hold a
        // number, so a phone on one is refused unless the operator has passed promoteToTakeaway — the
        // owner's decision that naming a number is itself the statement that the customer is taking the
        // order away. Making the UI decide this would mean the legal contour lives in a view, and the
        // next screen to write this column would not know about it.
        if (wantsPhone && order.OrderType != OrderType.Takeaway && !promoteToTakeaway)
            throw new ValidationFailureException(
                "Телефон сохраняется только для заказа «С собой». Смените тип выдачи, чтобы записать номер.");

        if (wantsPhone)
        {
            // Validated as Takeaway in both branches, because that is the mode the number is being stored
            // under — including the branch where it already was one. ContactPhoneRule drops the value
            // without reading it for any other mode, so passing the real mode here would silently
            // discard a number the operator just typed.
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

    /// <summary>
    /// Voids a sale: the order leaves the revenue, the money goes back, and — only if the operator
    /// says nothing was made — the stock write-off is undone. Everything lands in one transaction,
    /// because a voided order with the cash still in the drawer (or the stock restored on an order
    /// that is still counted) is worse than either outcome on its own.
    /// </summary>
    public async Task CancelOrderAsync(
        Guid orderId,
        string? reason = null,
        StockDisposition stock = StockDisposition.LeaveWrittenOff,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Same shape as CheckoutService: the refund rows, the stock movements and the status change
        // are one fact about the till, and a partial write of it is the definition of books that do
        // not close. StockPlanner.ReverseAsync can also refuse (an order-scoped receipt in the
        // journal), and the transaction is what makes that refusal leave nothing behind.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        // The only guard left. "Already cancelled" is what makes cancellation single-shot: a second
        // tap must not refund the money twice, and it is also the only state in which the money has
        // provably already left the drawer.
        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Заказ уже отменён.");

        var now = timeProvider.GetUtcNow();
        var operatorReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        // A paid order CAN be cancelled — it used to be refused, which was the only thing keeping
        // the sale in the books while the cash stayed in the drawer. Cancelling now takes the money
        // with it: the refund mirrors the original payment methods, so the drawer loses exactly what
        // it took and the terminal is credited nothing it never paid out.
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

        // One history row carrying the whole story, not just the reason: the refund and the stock
        // disposition are the parts an auditor asks about, and the status field alone would show a
        // void without saying where the money went.
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

    /// <summary>
    /// Orders.CancellationReason and OrderStatusHistory.Comment are both 300 characters
    /// (AppDbContext.Catalog), so the composition is built once and truncated at the single place
    /// that knows the limit. Truncating at the end is deliberate: the money and the stock
    /// disposition are facts about the till, and the operator's free text is the only part that can
    /// afford to lose its tail.
    /// </summary>
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
                // Names when the catalogue still has them, identifiers when it does not. Either way
                // the operator is told the return was partial: silently skipping would let the shelf
                // and the books disagree with nothing on screen to show it.
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

    /// <summary>
    /// Differential update: existing lines keep their identifiers, only quantities are updated,
    /// missing lines are removed and new lines are inserted. The old implementation deleted and
    /// recreated every line on each save, destroying line identity needed for audit/printing.
    /// <para>
    /// Lines are matched by <see cref="OrderLineKey"/> — product, modifier, variant AND the component
    /// signature — rather than by the three-field comparison that was written inline here. That
    /// comparison had already diverged from the one in the cart: it left the variant out of nothing at
    /// all but it also could not tell two bundles apart, so two different builds of the same bundle
    /// merged into one line at whichever price was added first. The whole reason the key moved into the
    /// core is that one function every caller computes the same way cannot drift apart again.
    /// </para>
    /// </summary>
    public async Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) throw new ValidationFailureException("Нельзя сохранить пустой заказ.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders
            .Include(current => current.Items)
                .ThenInclude(item => item.Components)
            .FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");
        if (order.Status != OrderStatus.InProgress)
            throw new ConflictException("Изменять можно только заказ, который еще готовится.");

        // The reconciled line set, kept separately from order.Items: a new line is tracked through
        // the DbSet (an entity pushed into the Items collection of a tracked order is tracked as
        // Modified, and EF then updates a row that does not exist yet), and EF only links it into
        // the loaded collection during DetectChanges.
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

            // BOTH LISTS, AND THE ORDER IS THE POINT — this lookup was the site of two defects in a row, and
            // both are the same mistake about which lines exist. A line added earlier in THIS call is
            // not in order.Items yet, so two identical lines in one save — the ordinary "the same
            // bundle twice in one order" case — could not find each other and were inserted as two
            // rows of quantity 1. Searching `resulting` alone fixes that and breaks the rest: the first
            // line of the call finds nothing there and every existing line gets rewritten as a new one.
            var existing = resulting.FirstOrDefault(Matches) ?? order.Items.FirstOrDefault(Matches);

            if (existing is null)
            {
                // A line ADDED to an open order gets its allowed price decided here, exactly as at
                // checkout, so the shift report's discount section cannot be walked around by editing
                // the order instead of selling it. For a bundle that allowed price is the bundle's OWN
                // price, read from the catalogue — the same number checkout would have used, from the
                // same loader, so the two cannot disagree about what a bundle may be sold for.
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

                // ListPriceKopecks is deliberately NOT rewritten here. It is the price the line was
                // ALLOWED to be sold at, written once when the line was created, and an edit that
                // recomputed it would erase the very signal this method exists to preserve: re-pricing
                // a line during the sale would become invisible, because the new charged price would be
                // written straight back into the allowed price and the report would have nothing left
                // to compare. A manual review is allowed to show up as a discount, not to be laundered
                // into "this is what it should have cost".
                SyncComponents(db, existing, incoming);
                resulting.Add(existing);
            }
        }

        var removed = order.Items.Where(item => resulting.All(keep => keep.Id != item.Id)).ToList();
        foreach (var item in removed) order.Items.Remove(item);
        if (removed.Count > 0) db.OrderItems.RemoveRange(removed);

        order.RecalculateTotal(resulting);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderNumber} updated: {Items} lines, total {Total}", order.OrderNumber, resulting.Count, order.TotalPrice);
    }

    /// <summary>
    /// The bundle's own price from the catalogue, or <c>null</c> when the line is not a live bundle.
    /// <para>
    /// ONE read per added line, and only for a line that has a composition at all. That is the price
    /// lookup rather than the sale-side resolver, deliberately: an order editor saves whatever the
    /// order holds, and a line whose template has been deleted since the sale has no price to be
    /// refused over — the answer is "the catalogue has no price for this", and the comparison falls
    /// back to the à la carte sum inside <see cref="OrderLinePricing.Resolve"/>.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The (dish, count per unit) pairs a line's composition folds into its merge key. An empty
    /// composition gives an empty signature, which is what makes an ordinary dish and a bundle of it
    /// two different lines rather than one line at the bundle's price.
    /// </summary>
    private static IEnumerable<(Guid productId, int quantity)> ToComponentSignature(OrderItem item) =>
        item.Components.Select(component => (component.ProductId, component.QuantityPerUnit));

    /// <summary>
    /// The composition in the shape <see cref="OrderLinePricing"/> and <see cref="ComboPricing"/> read.
    /// Only the identifier, the multiplicity and the slot price matter — the last one for the à la carte
    /// reference, which is the fallback when the catalogue has no price for the line; the names and the
    /// reference prices come back from the persisted rows when the line is saved.
    /// </summary>
    private static IReadOnlyList<CheckoutComponent> ToCheckoutComponents(OrderItem item) =>
        item.Components
            .Select(component => new CheckoutComponent(
                component.ProductId,
                component.ProductName,
                component.QuantityPerUnit,
                component.UnitPriceKopecks,
                component.ReferencePriceKopecks))
            .ToList();

    /// <summary>
    /// The composition snapshot for a line, written through the DbSet.
    /// <para>
    /// A snapshot is rewritten wholesale here, unlike the catalogue side which matches by identity:
    /// this is a record of what one sale was, and the caller has just stated what that sale is now.
    /// Keeping rows that were not mentioned would leave a component on the receipt that is no longer
    /// being sold — the composition would describe two different sales at once.
    /// </para>
    /// <para>
    /// The DbSet Add is the ONLY link, and the item's own <c>Components</c> collection is deliberately
    /// NOT appended to by hand. Doing both is a trap worth spelling out, because it is the mirror image
    /// of the one written out at <c>DraftOrderService</c>: adding a row whose foreign key points at an
    /// ALREADY TRACKED parent makes EF fix up the inverse navigation and append the entity to
    /// <c>item.Components</c> by itself, so pushing the same instance in again leaves the collection
    /// holding every snapshot twice.
    /// <para>
    /// That was measured here rather than reasoned about: two slots came back as four, and the
    /// consequence is far worse than a wrong count on screen — the doubled composition is what the
    /// merge key is built from, so two halves of one bundle stopped matching each other and every
    /// identical pair of lines became two rows. The receipt would have printed the composition twice
    /// over; the till would have sold two of everything.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Replaces the composition of a line that already exists: what the caller sent is what the sale is
    /// now, so the previous snapshot rows go and the current ones take their place.
    /// </summary>
    private static void SyncComponents(AppDbContext db, OrderItem existing, OrderItem incoming)
    {
        if (existing.Components.Count == 0 && incoming.Components.Count == 0) return;

        foreach (var stale in existing.Components.ToList())
        {
            // Removed through the DbSet as well: the rows are tracked entities of the loaded
            // composition, and detaching them from the collection alone would leave them in the change
            // tracker as unchanged — the receipt would keep printing a slot that is no longer sold.
            db.OrderItemComponents.Remove(stale);
            existing.Components.Remove(stale);
        }

        WriteComponents(db, existing, incoming);
    }
}
