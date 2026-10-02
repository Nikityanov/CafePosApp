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
        // manager physically counts is PaymentsCash − RefundsCash, and it is named ONCE, here, as
        // ShiftStats.ExpectedCashNow — not computed at each point of presentation, because a screen
        // that subtracts the two halves itself is a second definition of the same figure, and a
        // second definition is how two reports end up disagreeing about the drawer.
        var payments = await ShiftPayments.ReadAsync(db, shiftId, cancellationToken);

        // The frozen count comes off the shift row itself. AsNoTracking on purpose: this is a
        // report, and nothing here may accidentally mark a shift modified. A shift that was never
        // counted yields null — which is a fact of its own (every shift that predates the feature is
        // in that state) and NOT the same thing as a counted zero.
        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == shiftId, cancellationToken);
        var reconciliation = ShiftReconciliation.Read(shift);
        // Staleness lives here because this is the one place both numbers exist. A refund taken
        // against a closed shift moves the live figure and leaves the frozen snapshot alone, so the
        // two disagree from that moment on — and that disagreement is exactly what a manager has to
        // be shown, rather than a snapshot quietly rewritten to match the new reality.
        var isStale = ShiftReconciliation.IsStale(reconciliation, payments.CashInDrawerKopecks);

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
            Money.FromKopecks(payments.RefundedKopecks),
            Money.FromKopecks(payments.CashInDrawerKopecks),
            reconciliation,
            isStale);
    }

    /// <summary>
    /// The shift's product breakdown: one row per distinct product × modifier × variant.
    /// </summary>
    /// <remarks>
    /// The section of each dish comes from <c>Products</c> → <c>Categories</c>, and that link must be
    /// OUTER. <c>OrderItem</c> stores no category, so the join is the only source; a line whose
    /// product row cannot be reached, or whose product has no section, is still a sale and still
    /// belongs in the report.
    /// <para>
    /// <b>THE JOIN THAT DELETED SALES.</b> <c>Join(...)</c> with a cast-to-nullable key was believed
    /// to give a LEFT JOIN. It does not — EF Core kept it INNER, so every line whose product had
    /// <c>CategoryId == null</c> vanished from the breakdown while still counting in
    /// <c>ItemsCount</c> and in revenue. Measured on the emulator: a shift that sold 30 items showed
    /// 14, the missing 16 being exactly the unfiled dishes, with nothing on screen to say so. The
    /// symptom is the worst kind — every figure on the page looks reasonable, and the one number
    /// that cannot be checked against anything else is quietly short.
    /// <para>
    /// The fix is <c>SelectMany(..., DefaultIfEmpty())</c>, which EF translates to a real LEFT JOIN.
    /// Note what is NOT the fix: <c>GroupJoin</c> alone, and <c>GroupJoin</c> over a subquery with
    /// the key already cast. Both compiled, both looked like an outer join, and both had to be thrown
    /// away — the first because <c>DefaultIfEmpty</c> never reached the translator, the second
    /// because EF emitted a correlated <c>APPLY</c>, which SQLite refuses outright.
    /// <para>
    /// The section is the dish's CURRENT one, not the one it had when it was sold. That is a
    /// deliberate trade and it is not the same trade <see cref="Models.OrderItem.ProductName"/>
    /// makes: the name is snapshotted because a rename must not rewrite what a customer was charged,
    /// whereas a manager grouping yesterday's sales by where a dish sits TODAY is what "which
    /// section is this performing in" means. A dish moved between sections regroups its history, and
    /// the alternative — a snapshot column — would have needed a migration and would still be wrong
    /// for the question actually being asked.
    /// </para>
    /// <para>
    /// Grouping happens AFTER the join and carries the section, so a dish name that occurs in two
    /// sections stays two rows and an unfiled dish needs no special case in SQL. There is
    /// deliberately NO <c>ORDER BY</c>: ordering lives in <see cref="ProductAnalyticsProjection"/>
    /// so a screen which forgets to sort cannot be left holding a stale order from SQL.
    /// </para>
    /// </remarks>
    public async Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // ONE genuine inner join: a line whose order is not a completed order of THIS shift is not
        // this shift's sale, and that is the scope of the whole report.
        //
        // The product lookup below is SelectMany(..., DefaultIfEmpty()) — a LEFT JOIN EF actually
        // emits. It is NOT written as Join with nullable cast keys: that form compiled, looked right
        // and was silently INNER, which deleted every unfiled dish from the breakdown while it still
        // counted in ItemsCount. Measured on the emulator: a shift that sold 30 items showed 14, and
        // the cards at the top of the page — which read a different query — said 30 the whole time.
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
