using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// Money through one till during one shift: what was collected and what was given back, each split
/// by method. Shared by the shift report (<c>GetShiftStatsAsync</c>) and the CSV export so the two
/// cannot grow different definitions of "принято оплат". Grouping happens in SQL: a shift holds a
/// bounded number of payments, but the alternative — loading every row and folding in memory — is
/// the shape that degrades into a per-row round trip once a real day of data is in the table.
/// <para>
/// Revenue is deliberately NOT part of this: the shift report keeps counting
/// <c>SUM(Orders.TotalKopecks)</c> over completed orders, so a day's revenue never depends on
/// payments having been recorded.
/// </para>
/// <para>
/// The join carries NO status filter, and that is exactly right rather than an oversight: a refund
/// taken during the next shift still belongs to the drawer the money left. Booking it into the new
/// shift would be the lie — the new drawer never held the cash.
/// </para>
/// <para>
/// WHAT THIS IS NOT: the drawer. It never was, once a till can be opened with change in it, and
/// <see cref="CashLedger"/> is where that figure lives.
/// </para>
/// </summary>
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
        /// <summary>
        /// Cash taken in, less cash handed back — the payments side of the drawer figure ONLY.
        /// <para>
        /// It is NOT the drawer, and it no longer offers one. It was, until the opening float and
        /// collection existed, and the difference is the whole reason <see cref="CashLedger"/>
        /// exists: this pair of numbers is right for a till opened empty and never emptied, and wrong
        /// the moment anybody puts change in or carries cash out. Anything a human will read comes
        /// from <see cref="CashLedger.Totals.InDrawerKopecks"/>.
        /// </para>
        /// <para>
        /// What this pair CAN guarantee is non-negativity. The join above takes this shift's payments
        /// and this shift's refunds from the shift's OWN orders, so the per-order per-method
        /// inequality <c>refunded &lt;= collected</c> that
        /// <c>PaymentRecorder.AllocateMirroredSlices</c> guarantees sums to the same inequality for
        /// the shift. A refund pressed during a later shift on an order belonging to this one is
        /// still this shift's refund — that is the attribution the join performs — so it lowers the
        /// figure exactly as much as it raised it, and never below zero. The drawer as a whole has
        /// no such guarantee; see <see cref="CashLedger"/>.
        /// </para>
        /// </summary>
        public long PaymentsOnlyKopecks => CashKopecks - RefundsCashKopecks;
    }
}
