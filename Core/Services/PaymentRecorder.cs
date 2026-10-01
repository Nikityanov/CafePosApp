using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>
/// The one place where money is recorded against an order: it adds the ledger row AND moves
/// <see cref="Order.PaidKopecks"/>. Two paths record payments (checkout and
/// <c>IOrderService.AddPaymentAsync</c>) and they must not drift, because the scalar is what every
/// screen reads and the rows are what the shift report and an auditor reconcile against; a change
/// that touched only one of the two would leave the books open or closed by exactly one payment.
/// <para>
/// It never calls <c>SaveChangesAsync</c>. Checkout runs inside a transaction that also writes the
/// order and the stock write-off, and the caller's SaveChanges/Commit is the only commit point —
/// saving here would flush the half-built order on its own and defeat that transaction.
/// </para>
/// <para>
/// It also never pushes the row into <c>order.Payments</c>. The order is normally an already
/// tracked entity, and an entity pushed into the loaded collection of a tracked parent is tracked
/// as Modified: EF then issues an UPDATE for a row that does not exist yet (the bug recorded in
/// DraftOrderService and in UpdateOrderAsync). Going through the DbSet marks the row Added, and EF
/// orders the INSERT after its parent.
/// </para>
/// </summary>
internal static class PaymentRecorder
{
    /// <summary>
    /// Records <paramref name="amount"/> rubles against <paramref name="order"/> and returns the
    /// ledger row. Nothing is written to the database — the caller saves.
    /// </summary>
    public static OrderPayment Record(
        AppDbContext db,
        Order order,
        decimal amount,
        PaymentMethod method,
        DateTimeOffset paidAt,
        ILogger logger)
    {
        // State guards before the amount guard: what the operator needs to know first is which
        // part of the flow is wrong (a dead order, an already settled one), not that a number was
        // out of range on top of it.
        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Нельзя принять оплату по отменённому заказу.");
        if (order.BalanceKopecks <= 0)
            throw new ConflictException("Заказ уже оплачен.");

        // Cap at the balance, never record a surplus. A POS is handed a 1000 ₽ note for a 660 ₽
        // order: recording 1000 would break the till arithmetic (1000 in, 340 change out, 1000 in
        // the ledger) and there is no till/petty-cash model to absorb the difference. The payment
        // sheet shows the change itself and passes only the applied amount; this is the defensive
        // clamp that keeps the books true no matter what a caller does.
        var applied = Math.Min(Money.ToKopecks(amount), order.BalanceKopecks);
        if (applied <= 0)
            throw new ValidationFailureException("Сумма оплаты должна быть больше нуля.");

        var payment = new OrderPayment
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            AmountKopecks = applied,
            Method = method,
            PaidAt = paidAt
        };

        db.OrderPayments.Add(payment);
        order.PaidKopecks += applied;

        logger.LogInformation(
            "Order {OrderNumber}: accepted {Amount} ₽ by {Method}, {Balance} ₽ left",
            order.OrderNumber, payment.Amount, method, Money.FromKopecks(order.BalanceKopecks));
        return payment;
    }
}
