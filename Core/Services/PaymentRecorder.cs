using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>
/// The one place where the order's net cash moves: it adds the ledger row AND moves
/// <see cref="Order.PaidKopecks"/>. There are now four writers — checkout,
/// <c>IOrderService.AddPaymentAsync</c>, <c>IOrderService.RefundAsync</c> and the automatic refund
/// inside a cancellation — and they must not drift, because the scalar is what every screen reads
/// and the rows are what the shift report and an auditor reconcile against; a change that touched
/// only one of them would leave the books open or closed by exactly one movement.
/// <para>
/// <see cref="Record"/> adds and <see cref="Refund"/> subtracts, and they deliberately invert on
/// all three behaviours: Record throws on a cancelled order, throws when there is no balance left
/// and ADDS; Refund throws unless the order is closed, clamps to what was collected and
/// SUBTRACTS. Two methods rather than one method with a sign — a negative amount must never reach
/// <c>Money.ToKopecks</c>, because a negative <c>AmountKopecks</c> stored in the ledger would
/// break the positive-amounts rule that every sum and report depends on, and the direction belongs
/// in <c>IsRefund</c> anyway.
/// </para>
/// <para>
/// Neither method calls <c>SaveChangesAsync</c>. Checkout runs inside a transaction that also
/// writes the order and the stock write-off, and a cancellation refunds inside the transaction that
/// also voids the order — the caller's SaveChanges/Commit is the only commit point. Saving here
/// would flush the half-built work on its own and defeat that transaction.
/// </para>
/// <para>
/// Neither pushes the row into <c>order.Payments</c>. The order is normally an already
/// tracked entity, and an entity pushed into the loaded collection of a tracked parent is tracked
/// as Modified: EF then issues an UPDATE for a row that does not exist yet (the bug recorded in
/// DraftOrderService and in UpdateOrderAsync). Going through the DbSet marks the row Added, and EF
/// orders the INSERT after its parent.
/// </para>
/// </summary>
internal static class PaymentRecorder
{
    /// <summary>Matches the declared length of <c>OrderPayment.Note</c>.</summary>
    private const int MaxNoteLength = 300;

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

    /// <summary>
    /// Books <paramref name="kopecks"/> back out of the till and returns the ledger rows (one per
    /// source payment it mirrors). Nothing is written to the database — the caller saves.
    /// <para>
    /// WHY the method comes from the ledger and never from the operator: a POS has two tills — the
    /// drawer and the card terminal — and a refund has to leave the one the money arrived in. If
    /// 200 ₽ came in cash and 220 ₽ by card, a 420 ₽ refund booked as cash leaves the drawer 220 ₽
    /// short at the count and credits the terminal money it never paid out, and nothing in the app
    /// would ever notice. So the walk below is FIFO over the order's collected payments, which also
    /// gives <c>refunded(method) &lt;= collected(method)</c> for free: no per-method balance can go
    /// negative without someone having to check it. There is deliberately no operator-chosen refund
    /// method in this iteration — card payments here are a declared figure with no terminal behind
    /// them, so a chosen method would be a bookkeeping entry with nothing physical to reconcile.
    /// </para>
    /// <para>
    /// <paramref name="allowUncompletedOrder"/> exists because the status guard is a rule about the
    /// standalone refund operation, not about the money. Money goes back after the goods left the
    /// bar, which is why a refund on a Ready order is refused: the goods have not left yet, so
    /// cancelling the order is the operator's decision, not refunding a finished sale. A
    /// cancellation is the strictly stronger action (it voids the sale and the shift report drops
    /// the order), so it must not be blocked by the guard of the refund it performs on the way.
    /// </para>
    /// </summary>
    public static IReadOnlyList<OrderPayment> Refund(
        AppDbContext db,
        Order order,
        long kopecks,
        string reason,
        DateTimeOffset at,
        ILogger logger,
        bool allowUncompletedOrder = false)
    {
        // Same ordering of concerns as Record: which part of the flow is wrong first, the number
        // second.
        if (!allowUncompletedOrder && order.Status != OrderStatus.Completed)
            throw new ConflictException("Возвратить деньги можно только по закрытому заказу: товар уже отдан.");
        if (kopecks <= 0)
            throw new ValidationFailureException("Сумма возврата должна быть больше нуля.");

        // Cap at what was actually collected. This one guard is what keeps Order.PaidKopecks from
        // ever going negative, which in turn keeps BalanceKopecks, PaymentState and the
        // PaidKopecks <= TotalKopecks assertion honest — a scalar below zero would read as a debt
        // larger than the order and as "not paid" on a sale that is already finished.
        var requested = Math.Min(kopecks, order.PaidKopecks);
        if (requested <= 0)
            throw new ConflictException("По заказу нечего возвращать.");
        if (requested < kopecks)
            logger.LogWarning(
                "Order {OrderNumber}: refund of {Requested} ₽ capped to the {Applied} ₽ still collected",
                order.OrderNumber, Money.FromKopecks(kopecks), Money.FromKopecks(requested));

        var slices = AllocateMirroredSlices(db, order, requested);
        if (slices.Count == 0)
            // Unreachable through the app (every paid order has at least one ledger row), but if the
            // scalar ever says money was collected and the ledger cannot say by which method, the
            // honest answer is to refuse — not to invent a method the till never used.
            throw new ConflictException("Не удалось определить, каким способом была оплата: возврат невозможен.");

        var note = TruncateNote(reason);
        var payments = new List<OrderPayment>(slices.Count);
        foreach (var slice in slices)
        {
            // The cap is applied again per slice, which is what makes the invariant hold no matter
            // what the caller asks for: the slices sum to at most what is left, so the scalar
            // cannot be driven below zero even if the ledger and the scalar disagreed.
            var applied = Math.Min(slice.Kopecks, order.PaidKopecks);
            if (applied <= 0) break;

            var payment = new OrderPayment
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                AmountKopecks = applied,
                Method = slice.Method,
                PaidAt = at,
                IsRefund = true,
                Note = note
            };

            db.OrderPayments.Add(payment);
            order.PaidKopecks -= applied;
            payments.Add(payment);

            logger.LogInformation(
                "Order {OrderNumber}: refunded {Amount} ₽ by {Method}, {Remaining} ₽ of the order still stands",
                order.OrderNumber, payment.Amount, slice.Method, Money.FromKopecks(order.PaidKopecks));
        }

        return payments;
    }

    /// <summary>One slice of a refund: the method to book it under and how much of it.</summary>
    private readonly record struct Slice(PaymentMethod Method, long Kopecks);

    /// <summary>
    /// OrderPayment.Note is declared as 300 characters (AppDbContext.Catalog), and SQLite would
    /// happily store a page of text in it anyway — so the clamp lives here rather than in the
    /// column, where it would only show up as a value that disagrees with the model.
    /// </summary>
    private static string? TruncateNote(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= MaxNoteLength ? trimmed : trimmed[..MaxNoteLength];
    }

    /// <summary>
    /// Splits a refund across the methods the money actually arrived in, oldest payment first, and
    /// never more than a given source payment still holds.
    /// <para>
    /// The refunds that were already booked are replayed against the same sources rather than being
    /// summarised per method, and that is what makes a SECOND refund on the same order stay inside
    /// the original payments: refund 100 of a 200 ₽ cash payment, then ask for 150 more, and the 150
    /// comes out of what is left of the cash payment and then of the card one — it does not bill the
    /// drawer for a second 100 it never received.
    /// </para>
    /// <para>
    /// Consumption is scoped to the source's own METHOD, not just to "some source with money left".
    /// A cash refund can only ever have come out of a cash payment, so matching on the method keeps
    /// the per-till balance exact rather than merely correct in total, and makes the result
    /// independent of the order two refunds that share a timestamp happen to be read in.
    /// </para>
    /// </summary>
    private static List<Slice> AllocateMirroredSlices(AppDbContext db, Order order, long kopecks)
    {
        // Synchronous on purpose: Refund is a synchronous unit of work by contract (the caller is
        // inside an async path and awaits it as one step). SQLite cannot ORDER BY a DateTimeOffset
        // column in SQL — the same reason the order lists and GetOrderPaymentsAsync sort in memory —
        // and the ledger of one order is small enough for that to be free. IX_OrderPayments_OrderId_
        // PaidAt serves the WHERE.
        var ledger = db.OrderPayments.Where(payment => payment.OrderId == order.Id).ToList();
        // A LINQ query goes to the database, which knows nothing about rows this context has Added
        // but not yet saved. Unioning the local ones is what makes a second Refund call in the same
        // unit of work see the first one's rows; the distinct keeps the already-tracked originals.
        var pending = db.OrderPayments.Local
            .Where(payment => payment.OrderId == order.Id && !ledger.Contains(payment))
            .ToList();

        var oldestFirst = ledger
            .Concat(pending)
            .OrderBy(payment => payment.PaidAt)
            .ThenBy(payment => payment.Id)
            .ToList();

        var sources = oldestFirst
            .Where(payment => !payment.IsRefund)
            .Select(payment => new Source(payment.Method, payment.AmountKopecks))
            .ToList();
        if (sources.Count == 0) return [];

        // Consume what was already given back, oldest refund first.
        foreach (var booked in oldestFirst.Where(payment => payment.IsRefund))
        {
            var outstanding = booked.AmountKopecks;
            while (outstanding > 0)
            {
                var source = sources.FirstOrDefault(candidate =>
                    candidate.Method == booked.Method && candidate.Left > 0);
                if (source is null) break;
                var taken = Math.Min(outstanding, source.Left);
                source.Left -= taken;
                outstanding -= taken;
            }
        }

        var slices = new List<Slice>();
        var remaining = kopecks;
        foreach (var source in sources)
        {
            if (remaining <= 0) break;
            var taken = Math.Min(remaining, source.Left);
            if (taken <= 0) continue;
            source.Left -= taken;
            remaining -= taken;
            slices.Add(new Slice(source.Method, taken));
        }

        return slices;
    }

    /// <summary>A source payment with the kopecks of it that have not been given back yet.</summary>
    private sealed class Source(PaymentMethod method, long kopecks)
    {
        public PaymentMethod Method { get; } = method;
        public long Left { get; set; } = kopecks;
    }
}
