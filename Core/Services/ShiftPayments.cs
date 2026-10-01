using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// Payments collected during one shift, grouped by method. Shared by the shift report
/// (<c>GetShiftStatsAsync</c>) and the CSV export so the two cannot grow different definitions of
/// "принято оплат". Grouping happens in SQL: a shift holds a bounded number of payments, but the
/// alternative — loading every row and folding in memory — is the shape that degrades into a
/// per-row round trip once a real day of data is in the table.
/// <para>
/// Revenue is deliberately NOT part of this: the shift report keeps counting
/// <c>SUM(Orders.TotalKopecks)</c> over completed orders, so a day's revenue never depends on
/// payments having been recorded.
/// </para>
/// </summary>
internal static class ShiftPayments
{
    public static async Task<Totals> ReadAsync(AppDbContext db, Guid shiftId, CancellationToken cancellationToken)
    {
        var byMethod = await db.OrderPayments.AsNoTracking()
            .Join(db.Orders.AsNoTracking().Where(order => order.ShiftId == shiftId),
                payment => payment.OrderId, order => order.Id,
                (payment, _) => new { payment.Method, payment.AmountKopecks })
            .GroupBy(row => row.Method)
            .Select(group => new { Method = group.Key, Count = group.Count(), Kopecks = group.Sum(row => row.AmountKopecks) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new Totals(
            byMethod.Sum(row => row.Count),
            byMethod.Sum(row => row.Kopecks),
            byMethod.Where(row => row.Method == PaymentMethod.Cash).Sum(row => row.Kopecks),
            byMethod.Where(row => row.Method == PaymentMethod.Card).Sum(row => row.Kopecks));
    }

    /// <summary>Amounts in kopecks — the DTO boundary converts to rubles with <c>Money</c>.</summary>
    internal readonly record struct Totals(int Count, long TotalKopecks, long CashKopecks, long CardKopecks);
}
