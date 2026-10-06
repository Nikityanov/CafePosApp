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
        var ledger = await CashLedger.ReadAsync(db, shiftId, cancellationToken);

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

    /// <summary>
    /// The shift's "Скидки" section: every line whose charged price is not the price it was allowed to
    /// be sold at.
    /// </summary>
    /// <remarks>
    /// <b>THE READ SIDE OF THE ONE CONTROL.</b> <see cref="OrderLinePricing"/> decides whether a price
    /// was overridden at the moment it is written; this is where anyone finds out. No standard asks
    /// for it — PCI DSS v4.0.1 has no occurrence of "discount" in 397 pages — and the argument is
    /// margin: a median restaurant runs 2.8% net, so a 1% discount on sales eats roughly 36% of it,
    /// while what the vendors ship starts at "the cashier may discount to 100%" (Lightspeed) or "Any
    /// User" (Toast). The recomputation that makes it work — "the components do not add up to the
    /// price" — is our own idea and not published practice; the published analogue is the before/after
    /// review at Oracle, Bank of America and Bitta. What it buys is the FIRST occurrence caught with no
    /// historical threshold to calibrate.
    /// <para>
    /// No reason code and no operator identity, both deliberately absent. A reason typed at the till
    /// becomes the first value anybody ever picks and only the aggregate analysis of the pattern is
    /// worth anything; and "who did it" needs staff entities, sign-in, PIN and permissions — a feature
    /// the size of this one. The report says what was given away, and stops there.
    /// </para>
    /// <para>
    /// The comparison itself is done in SQL because both figures are integer columns on the same row
    /// and there is nothing to fetch first — the whole section is one query, not a scan of the shift's
    /// orders with arithmetic folded in memory. The reference total is the exception: it is a SUM over
    /// a CHILD table per line, which SQLite cannot do in a correlated subquery here, so the
    /// compositions of the matching lines are loaded and folded here.
    /// </para>
    /// <para>
    /// <b>WHAT THIS SECTION DELIBERATELY DOES NOT SHOW, SO IT IS NOT REDISCOVERED AS A BUG.</b> A
    /// bundle that is sold at exactly its own price but deliberately cheaper than its parts does not
    /// appear here: the filter is on the price mismatch, which is the question this section answers.
    /// Surfacing those would be a SECOND section — "наборы выгоднее своих частей" — and a bundle
    /// priced at a loss on purpose would otherwise drown the override report. It is a real thing a
    /// manager may want, and it is deliberately not what this query returns.
    /// </para>
    /// </remarks>
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

        // Σ(ReferencePriceKopecks × QuantityPerUnit) per line, then × the line quantity: what the same
        // dishes would have cost on their own. Nullable all the way through, because a line with no
        // components has no "cheaper than its parts" figure and null is a different statement from 0 —
        // 0 would say the bundle cost exactly as much as its parts, which is a fact about a bundle and
        // not about an ordinary dish.
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
                // TextFormat.Plural, NOT a bare "заказов". On device this read «11:00–12:00 (1 заказов)»
                // for a single-order shift, and the green test run said nothing because the rule is a
                // formatting concern, not an aggregate. The helper exists precisely for this — its own
                // docblock records that "1 товаров" was written in two files and corrected in one — and
                // every count around this one on the same page already pluralises correctly, which is
                // exactly why the single wrong form survived a visual pass on a 411dp phone.
                return $"{group.Key:00}:00–{(group.Key + 1) % 24:00}:00 ({count} {TextFormat.Plural(count, "заказ", "заказа", "заказов")})";
            })
            .First();
    }

    /// <summary>Minimal projection used for duration and peak-hour analytics.</summary>
    private sealed record OrderTimeline(DateTimeOffset CreatedAt, DateTimeOffset? ReadyAt, DateTimeOffset? CompletedAt);
}
