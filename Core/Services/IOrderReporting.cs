using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// Reading a shift as numbers: aggregates, the product breakdown, and the overridden-price lines.
/// </summary>
/// <remarks>
/// <para>
/// The narrowest and most misused port before the split. <c>ShiftAnalyticsViewModel</c> calls two
/// methods on it and held an interface with twenty-one others, one of which closed the till — so a
/// reporting screen carried the authority to reconcile a drawer it only ever displays.
/// </para>
/// <para>
/// It is the reporting side, so it is also where the definitions live:
/// <see cref="ShiftStats.ExpectedCashNow"/> and <see cref="ShiftStats.Reconciliation"/> are computed
/// once here rather than by each screen, because a report that recomputes the difference is a second
/// definition of the same figure.
/// </para>
/// </remarks>
public interface IOrderReporting
{
    /// <summary>
    /// All shift aggregates. Single source of truth for the shift report and analytics.
    /// <para>
    /// It also carries the ONE definition of "what is in the drawer right now"
    /// (<see cref="ShiftStats.ExpectedCashNow"/>) and the frozen count comparison
    /// (<see cref="ShiftStats.Reconciliation"/>). Presentation subtracts nothing: a view model that
    /// recomputes the difference is a second definition, and a second definition is how a report
    /// ends up netting one meaning of "принято" against another.
    /// </para>
    /// </summary>
    Task<ShiftStats> GetShiftStatsAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift's product breakdown, one row per distinct product × modifier × variant, with the
    /// section each dish currently belongs to. UNSORTED on purpose: the breakdown is ordered by
    /// <see cref="CafePos.Core.Services.ProductAnalyticsProjection"/>, which is the one place the
    /// order is defined — an <c>ORDER BY</c> here would be a second definition that silently wins
    /// whenever a screen forgets to re-sort.
    /// </summary>
    Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift's lines whose charged price differs from the price they were allowed to be sold at:
    /// the "Скидки" section of the shift report.
    /// <para>
    /// This is where the price control becomes visible, and it is detection rather than prevention:
    /// nothing is refused at the till, and a manager reads this after the shift. Ordered by order
    /// number and then by the line's position in the order, so the section reads in the order the
    /// sales happened rather than in whatever order the query returned.
    /// </para>
    /// <para>
    /// Includes CANCELLED orders on purpose. A voided sale is where an overridden price is most worth
    /// seeing, and it is absent from the revenue — which is exactly why every row carries its
    /// <see cref="DiscountedLine.Status"/> and why the section must not be summed as if it were one
    /// number.
    /// </para>
    /// </summary>
    Task<List<DiscountedLine>> GetDiscountedLinesAsync(Guid shiftId, CancellationToken cancellationToken = default);
}
