using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>The one place where the order's net cash moves: it adds the ledger row AND moves `Order.PaidKopecks`.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

internal static class PaymentRecorder
{
    /// <summary>Matches the declared length of <c>OrderPayment.Note</c>.</summary>
    private const int MaxNoteLength = 300;

    /// <summary>Records rubles against and returns the ledger row. Nothing is written to the database — the caller saves.</summary>

    public static OrderPayment Record(
        AppDbContext db,
        Order order,
        decimal amount,
        PaymentMethod method,
        DateTimeOffset paidAt,
        ILogger logger)
    {
        /// <summary>State guards before the amount guard: what the operator needs to know first is which part of the flow is wrong (a dead order, an already settled one), not that a number was out of range on top of it.</summary>

        if (order.Status == OrderStatus.Cancelled)
            throw new ConflictException("Нельзя принять оплату по отменённому заказу.");
        if (order.BalanceKopecks <= 0)
            throw new ConflictException("Заказ уже оплачен.");

        /// <summary>Cap at the balance, never record a surplus.</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

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

    /// <summary>Books <paramref name="kopecks"/> back out of the till and returns the ledger rows (one per source payment it mirrors).</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

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

        /// <summary>Cap at what was actually collected.</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

        var requested = Math.Min(kopecks, order.PaidKopecks);
        if (requested <= 0)
            throw new ConflictException("По заказу нечего возвращать.");
        if (requested < kopecks)
            logger.LogWarning(
                "Order {OrderNumber}: refund of {Requested} ₽ capped to the {Applied} ₽ still collected",
                order.OrderNumber, Money.FromKopecks(kopecks), Money.FromKopecks(requested));

        var slices = AllocateMirroredSlices(db, order, requested);
        if (slices.Count == 0)
            /// <summary>Unreachable through the app (every paid order has at least one ledger row), but if the scalar ever says money was collected and the ledger cannot say…</summary>
            /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

            throw new ConflictException("Не удалось определить, каким способом была оплата: возврат невозможен.");

        var note = TruncateNote(reason);
        var payments = new List<OrderPayment>(slices.Count);
        foreach (var slice in slices)
        {
            /// <summary>The cap is applied again per slice, which is what makes the invariant hold no matter what the caller asks for: the slices sum to at most what is left,…</summary>
            /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

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

    /// <summary>OrderPayment.Note is declared as 300 characters (AppDbContext.Catalog), and SQLite would happily store a page of text in it anyway — so the clamp live…</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private static string? TruncateNote(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length <= MaxNoteLength ? trimmed : trimmed[..MaxNoteLength];
    }

    /// <summary>Splits a refund across the methods the money actually arrived in, oldest payment first, and never more than a given source payment still holds.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    private static List<Slice> AllocateMirroredSlices(AppDbContext db, Order order, long kopecks)
    {
        /// <summary>Synchronous on purpose: Refund is a synchronous unit of work by contract (the caller is inside an async path and awaits it as one step).</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

        var ledger = db.OrderPayments.Where(payment => payment.OrderId == order.Id).ToList();
        /// <summary>A LINQ query goes to the database, which knows nothing about rows this context has Added but not yet saved.</summary>
        /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

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
