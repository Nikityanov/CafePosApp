using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>
/// Taking money back after the fact. The refund itself — how much, against which source payments and
/// how the scalar moves — belongs to <see cref="PaymentRecorder"/>, the single place where the
/// order's net cash moves; this file is the policy around it.
/// </summary>
public sealed partial class OrderService
{
    /// <summary>
    /// Refunds part or all of a finished order. <paramref name="amount"/> is rubles, clamped to what
    /// the order still holds, and is mirrored back out of the methods the money originally arrived
    /// in — there is no payment-method argument, because the drawer and the terminal are two real
    /// tills and a refund has to leave the one that was credited. An order stays
    /// <see cref="OrderStatus.Completed"/>: a partial refund returns part of the money for a sale
    /// that did happen, and voiding the whole sale is what cancelling is for.
    /// </summary>
    public async Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Tracked on purpose: the scalar moved by the recorder has to be picked up as an UPDATE.
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        // Restricting refunds to Completed is load-bearing, not a UX preference: it is what keeps
        // AdvanceStatusAsync's IsFullyPaid guard sound. That guard relies on an order's total being
        // FROZEN from the moment it becomes Ready (UpdateOrderAsync refuses anything that is not
        // InProgress), so a paid order can never become underpaid — and a refund lowers
        // PaidKopecks, so allowing it before the goods leave the bar would make a Ready order
        // underpaid with no way back. CloseShiftAsync's open-order money math is untouched for the
        // same reason.
        PaymentRecorder.Refund(db, order, Money.ToKopecks(amount), reason, timeProvider.GetUtcNow(), logger);

        await db.SaveChangesAsync(cancellationToken);
        return order;
    }
}
