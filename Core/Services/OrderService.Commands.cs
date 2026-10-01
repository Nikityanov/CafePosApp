using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Status transitions, cancellation and item editing of an order.</summary>
public sealed partial class OrderService
{
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
        // underpaid, because the only writable total cannot change afterwards. That is also why
        // a refund (which would lower PaidKopecks) is a separate feature: the first one written
        // must revisit this guard, because "paid" would stop being permanent.
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

    public async Task CancelOrderAsync(Guid orderId, string? reason = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");
        if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled)
            throw new ConflictException("Этот заказ уже закрыт.");

        // One tap of "Отменить заказ" must not be an escape hatch out of the payment block: it
        // would delete a paid order from the day's revenue while the cash stays in the drawer.
        // There is no refunds feature yet, so refusing here is the only guard that keeps the sale
        // in the books; the first refund implementation has to come with its own reversal entries
        // and may then relax this.
        if (order.PaidKopecks > 0)
            throw new ConflictException("Нельзя отменить оплаченный заказ: сначала верните оплату.");

        var now = timeProvider.GetUtcNow();
        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = now;
        order.CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        db.OrderStatusHistory.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            Status = OrderStatus.Cancelled,
            ChangedAt = now,
            Comment = order.CancellationReason
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Order {OrderNumber} cancelled ({Reason})", order.OrderNumber, order.CancellationReason ?? "no reason");
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
