using CafePos.Core.Common;
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
    /// </summary>
    public async Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) throw new ValidationFailureException("Нельзя сохранить пустой заказ.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders
            .Include(current => current.Items)
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
            var existing = order.Items.FirstOrDefault(item =>
                item.ProductId == incoming.ProductId
                && item.SelectedModifierName == incoming.SelectedModifierName
                && item.SelectedVariantName == incoming.SelectedVariantName);

            if (existing is null)
            {
                var added = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    ProductId = incoming.ProductId,
                    ProductName = incoming.ProductName,
                    Price = incoming.Price,
                    Quantity = incoming.Quantity,
                    SelectedModifierName = incoming.SelectedModifierName,
                    SelectedVariantName = incoming.SelectedVariantName
                };
                db.OrderItems.Add(added);
                resulting.Add(added);
            }
            else
            {
                existing.Quantity = incoming.Quantity;
                existing.Price = incoming.Price;
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
}
