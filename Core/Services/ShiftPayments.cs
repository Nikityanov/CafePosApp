using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Money through one till during one shift: what was collected and what was given back, each split by method.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

internal static class ShiftPayments
{
    public static async Task<Totals> ReadAsync(AppDbContext db, Guid shiftId, CancellationToken cancellationToken)
    {
        var byMethod = await db.OrderPayments.AsNoTracking()
            .Join(db.Orders.AsNoTracking().Where(order => order.ShiftId == shiftId),
                payment => payment.OrderId, order => order.Id,
                (payment, _) => new { payment.Method, payment.IsRefund, payment.AmountKopecks })
            .GroupBy(row => new { row.Method, row.IsRefund })
            .Select(group => new
            {
                Method = group.Key.Method,
                IsRefund = group.Key.IsRefund,
                Count = group.Count(),
                Kopecks = group.Sum(row => row.AmountKopecks)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var collected = byMethod.Where(row => !row.IsRefund).ToList();
        var refunds = byMethod.Where(row => row.IsRefund).ToList();

        return new Totals(
            collected.Sum(row => row.Count),
            collected.Sum(row => row.Kopecks),
            collected.Where(row => row.Method == PaymentMethod.Cash).Sum(row => row.Kopecks),
            collected.Where(row => row.Method == PaymentMethod.Card).Sum(row => row.Kopecks),
            refunds.Sum(row => row.Kopecks),
            refunds.Where(row => row.Method == PaymentMethod.Cash).Sum(row => row.Kopecks),
            refunds.Where(row => row.Method == PaymentMethod.Card).Sum(row => row.Kopecks));
    }

    /// <summary>Amounts in kopecks — the DTO boundary converts to rubles with <c>Money</c>.</summary>
    internal readonly record struct Totals(
        int Count,
        long TotalKopecks,
        long CashKopecks,
        long CardKopecks,
        long RefundedKopecks,
        long RefundsCashKopecks,
        long RefundsCardKopecks)
    {
        /// <summary>Cash taken in, less cash handed back — the payments side of the drawer figure ONLY.</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        public long PaymentsOnlyKopecks => CashKopecks - RefundsCashKopecks;
    }
}
