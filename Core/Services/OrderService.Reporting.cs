using CafePos.Core.Common;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// All reporting aggregates in one place. Revenue/counts are computed in SQL over the integer
/// kopeck columns; only the timestamp based metrics (peak hour, average durations) are folded
/// in memory because SQLite cannot translate localization-aware date math.
/// </summary>
public sealed partial class OrderService
{
    public async Task<ShiftStats> GetShiftStatsAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var byStatus = await db.Orders.AsNoTracking()
            .Where(order => order.ShiftId == shiftId)
            .GroupBy(order => order.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
                RevenueKopecks = group.Sum(order => order.TotalKopecks)
            })
            .ToListAsync(cancellationToken);

        var allCount = byStatus.Sum(row => row.Count);
        var completed = byStatus.FirstOrDefault(row => row.Status == OrderStatus.Completed);
        var completedCount = completed?.Count ?? 0;
        var revenue = Money.FromKopecks(completed?.RevenueKopecks ?? 0);

        var itemsCount = await db.OrderItems.AsNoTracking()
            .Join(db.Orders.Where(order => order.ShiftId == shiftId && order.Status == OrderStatus.Completed),
                item => item.OrderId, order => order.Id, (item, order) => item)
            .SumAsync(item => item.Quantity, cancellationToken);

        var timestamps = await db.Orders.AsNoTracking()
            .Where(order => order.ShiftId == shiftId && order.Status == OrderStatus.Completed)
            .Select(order => new OrderTimeline(order.CreatedAt, order.ReadyAt, order.CompletedAt))
            .ToListAsync(cancellationToken);

        var preparation = timestamps
            .Where(row => row.ReadyAt.HasValue)
            .Select(row => (row.ReadyAt!.Value - row.CreatedAt).TotalMinutes)
            .ToList();
        var completion = timestamps
            .Where(row => row.CompletedAt.HasValue)
            .Select(row => (row.CompletedAt!.Value - row.CreatedAt).TotalMinutes)
            .ToList();

        // "Принято оплат" is a separate aggregate from Revenue on purpose. Revenue stays
        // SUM(TotalKopecks) over completed orders, so a day's revenue never depends on payments
        // having been recorded; this is what the till actually took, split by method for the cash
        // count. The two coincide for completed orders (they cannot become Ready unpaid) and the
        // payment total additionally covers orders paid in advance while still cooking.
        //
        // Both halves stay GROSS and separate: the four Payments* fields keep meaning "what came
        // in", unchanged and in this order, and the refunds are additive on top. The number a
        // manager physically counts is PaymentsCash − RefundsCash, and it is computed at the point
        // of presentation (the report screen and the CSV) rather than stored here, so there is one
        // subtraction and not two.
        var payments = await ShiftPayments.ReadAsync(db, shiftId, cancellationToken);

        return new ShiftStats(
            allCount,
            completedCount,
            byStatus.FirstOrDefault(row => row.Status == OrderStatus.Cancelled)?.Count ?? 0,
            byStatus.FirstOrDefault(row => row.Status is OrderStatus.InProgress or OrderStatus.Ready)?.Count ?? 0,
            revenue,
            completedCount == 0 ? 0 : Money.Round(revenue / completedCount),
            itemsCount,
            preparation.Count == 0 ? 0 : preparation.Average(),
            completion.Count == 0 ? 0 : completion.Average(),
            BuildPeakHour(timestamps),
            payments.Count,
            Money.FromKopecks(payments.CashKopecks),
            Money.FromKopecks(payments.CardKopecks),
            Money.FromKopecks(payments.TotalKopecks),
            Money.FromKopecks(payments.RefundsCashKopecks),
            Money.FromKopecks(payments.RefundsCardKopecks),
            Money.FromKopecks(payments.RefundedKopecks));
    }

    public async Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.OrderItems.AsNoTracking()
            .Join(db.Orders.Where(order => order.ShiftId == shiftId && order.Status == OrderStatus.Completed),
                item => item.OrderId, order => order.Id, (item, order) => item)
            .GroupBy(item => new { item.ProductName, item.SelectedModifierName, item.SelectedVariantName })
            .Select(group => new
            {
                group.Key.ProductName,
                group.Key.SelectedModifierName,
                group.Key.SelectedVariantName,
                Quantity = group.Sum(item => item.Quantity),
                RevenueKopecks = group.Sum(item => item.PriceKopecks * item.Quantity)
            })
            .OrderByDescending(row => row.Quantity)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ProductAnalyticsRowData(
            row.ProductName,
            DescribeModifier(row.SelectedModifierName, row.SelectedVariantName),
            row.Quantity,
            Money.FromKopecks(row.RevenueKopecks))).ToList();
    }

    private static string DescribeModifier(string? modifierName, string? variantName)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(variantName)) parts.Add(variantName!);
        if (!string.IsNullOrWhiteSpace(modifierName)) parts.Add(modifierName!);
        return parts.Count == 0 ? "Без модификатора" : string.Join(" / ", parts);
    }

    private string BuildPeakHour(IReadOnlyCollection<OrderTimeline> timeline)
    {
        if (timeline.Count == 0) return "—";

        // Grouping by the local hour is done after the (small) projection is materialised,
        // because the stored values are UTC and SQLite cannot apply the local offset reliably.
        return timeline
            .GroupBy(row => row.CreatedAt.ToLocalTime().Hour)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key:00}:00–{(group.Key + 1) % 24:00}:00 ({group.Count()} заказов)")
            .First();
    }

    /// <summary>Minimal projection used for duration and peak-hour analytics.</summary>
    private sealed record OrderTimeline(DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt, DateTimeOffset? CompletedAt);
}
