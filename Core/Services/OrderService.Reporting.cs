using CafePos.Core.Common;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>All reporting aggregates in one place.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

        /// <summary>"Принято оплат" is a separate aggregate from Revenue on purpose.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var ledger = await CashLedger.ReadAsync(db, shiftId, cancellationToken);

        /// <summary>The frozen count comes off the shift row itself.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == shiftId, cancellationToken);
        var reconciliation = ShiftReconciliation.Read(shift);
        /// <summary>Staleness lives here because this is the one place both numbers exist.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var isStale = ShiftReconciliation.IsStale(reconciliation, ledger.InDrawerKopecks);

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
            ledger.Payments.Count,
            Money.FromKopecks(ledger.Payments.CashKopecks),
            Money.FromKopecks(ledger.Payments.CardKopecks),
            Money.FromKopecks(ledger.Payments.TotalKopecks),
            Money.FromKopecks(ledger.Payments.RefundsCashKopecks),
            Money.FromKopecks(ledger.Payments.RefundsCardKopecks),
            Money.FromKopecks(ledger.Payments.RefundedKopecks),
            Money.FromKopecks(ledger.FloatKopecks),
            Money.FromKopecks(ledger.PayoutKopecks),
            Money.FromKopecks(ledger.InDrawerKopecks),
            reconciliation,
            isStale);
    }

    /// <summary>The shift's product breakdown: one row per distinct product × modifier × variant.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        /// <summary>ONE genuine inner join: a line whose order is not a completed order of THIS shift is not this shift's sale, and that is the scope of the whole report.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var completed = db.Orders
            .Where(order => order.ShiftId == shiftId && order.Status == OrderStatus.Completed)
            .Select(order => order.Id);

        var items = db.OrderItems.AsNoTracking().Where(item => completed.Contains(item.OrderId));

        // LEFT JOIN: every line survives even when its product row is gone, and the section rides
        // the same outer join off the navigation rather than a second one.
        var lines = items
            .SelectMany(
                item => db.Products.AsNoTracking()
                    .Where(product => product.Id == item.ProductId)
                    .DefaultIfEmpty(),
                (item, product) => new
                {
                    item.ProductName,
                    item.SelectedModifierName,
                    item.SelectedVariantName,
                    item.Quantity,
                    item.PriceKopecks,
                    CategoryId = product == null ? (Guid?)null : product.CategoryId,
                    CategoryName = product == null || product.Category == null
                        ? null
                        : product.Category.Name
                });

        var rows = await lines
            .GroupBy(line => new
            {
                line.ProductName,
                line.SelectedModifierName,
                line.SelectedVariantName,
                line.CategoryId,
                line.CategoryName
            })
            .Select(group => new
            {
                group.Key.ProductName,
                group.Key.SelectedModifierName,
                group.Key.SelectedVariantName,
                group.Key.CategoryId,
                group.Key.CategoryName,
                Quantity = group.Sum(line => line.Quantity),
                RevenueKopecks = group.Sum(line => line.PriceKopecks * line.Quantity)
            })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ProductAnalyticsRowData(
            row.ProductName,
            DescribeModifier(row.SelectedModifierName, row.SelectedVariantName),
            row.Quantity,
            Money.FromKopecks(row.RevenueKopecks),
            row.CategoryId,
            row.CategoryName)).ToList();
    }

    /// <summary>The shift's "Скидки" section: every line whose charged price is not the price it was allowed to be sold at.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task<List<DiscountedLine>> GetDiscountedLinesAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Both completed and voided — see the remarks on IOrderService.GetDiscountedLinesAsync.
        var lines = await (
            from item in db.OrderItems.AsNoTracking()
            join order in db.Orders.AsNoTracking() on item.OrderId equals order.Id
            where order.ShiftId == shiftId && item.PriceKopecks != item.ListPriceKopecks
            select new
            {
                order.OrderNumber,
                order.Status,
                item.Id,
                item.ProductName,
                item.SelectedModifierName,
                item.SelectedVariantName,
                item.Quantity,
                item.PriceKopecks,
                item.ListPriceKopecks
            })
            .ToListAsync(cancellationToken);

        if (lines.Count == 0) return [];

        /// <summary>Σ(ReferencePriceKopecks × QuantityPerUnit) per line, then × the line quantity: what the same dishes would have cost on their own.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var itemIds = lines.Select(line => line.Id).ToList();
        var references = await db.OrderItemComponents.AsNoTracking()
            .Where(component => itemIds.Contains(component.OrderItemId))
            .Select(component => new { component.OrderItemId, component.ReferencePriceKopecks, component.QuantityPerUnit })
            .ToListAsync(cancellationToken);

        var referenceByItem = references
            .GroupBy(component => component.OrderItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(component => component.ReferencePriceKopecks * component.QuantityPerUnit));

        return lines
            .Select(line => new DiscountedLine(
                line.OrderNumber,
                line.Status,
                line.ProductName,
                line.SelectedModifierName,
                line.SelectedVariantName,
                line.Quantity,
                line.ListPriceKopecks,
                line.PriceKopecks,
                referenceByItem.TryGetValue(line.Id, out var reference) ? reference * line.Quantity : null))
            .OrderBy(line => line.OrderNumber)
            .ToList();
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
            .Select(group =>
            {
                var count = group.Count();
                // TextFormat.Plural, NOT a bare "заказов".
                // Почему так — `docs/decisions/orders.md`

                return $"{group.Key:00}:00–{(group.Key + 1) % 24:00}:00 ({count} {TextFormat.Plural(count, "заказ", "заказа", "заказов")})";
            })
            .First();
    }

    /// <summary>Minimal projection used for duration and peak-hour analytics.</summary>
    private sealed record OrderTimeline(DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt, DateTimeOffset? CompletedAt);
}
