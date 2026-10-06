using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// The shapes <see cref="IOrderReporting"/> returns.
/// </summary>
/// <remarks>
/// These were declared at the foot of <c>IOrderService.cs</c>, behind an interface they had nothing to
/// do with — a file named after one type holding five others is the same mistake the interface split
/// was fixing, one level down. They live here now because they are the reporting contract's own types
/// and reading them is how you know what a shift figure means.
/// </remarks>
public sealed record ShiftStats(
    int AllOrdersCount,
    int CompletedCount,
    int CancelledCount,
    int OpenCount,
    decimal Revenue,
    decimal AverageCheck,
    int ItemsCount,
    double AveragePreparationMinutes,
    double AverageCompletionMinutes,
    string PeakHour,
    // "Принято оплат": what the till actually recorded, split by method. Added at the end and
    // kept separate from Revenue on purpose — Revenue stays SUM(TotalKopecks) over completed
    // orders, so a shift's revenue never depends on payments having been recorded. The two agree
    // for completed orders (they cannot become Ready unpaid) and the payment total additionally
    // covers orders that were paid in advance and are still cooking.
    int PaymentsCount,
    decimal PaymentsCash,
    decimal PaymentsCard,
    decimal PaymentsTotal,
    // "Возвращено": money that left the till again, split by the method it left in. Appended at the
    // end and kept strictly additive on purpose — the four Payments* fields above stay GROSS, so
    // every existing reading of them ("what came in") is unchanged, and what is actually in the
    // drawer is PaymentsCash − RefundsCash. There is no refund count: nobody reconciles a count of
    // refunds, only money.
    decimal RefundsCash,
    decimal RefundsCard,
    decimal RefundsTotal,
    // "Внесено размена" and "Изъято на инкассацию": money that moved through the drawer by hand,
    // which is to say without a customer behind it. Placed here, immediately before
    // ExpectedCashNow, because they are two of the four terms that figure is made of — ExpectedCashNow
    // is FloatCash + PaymentsCash − RefundsCash − PayoutCash and a reader who cannot see the other
    // three has to trust it.
    //
    // Both are NET of correcting entries: a cancelled top-up lowers FloatCash rather than appearing
    // as a second, negative float. Reporting the gross and the correction separately was rejected —
    // an operator reconciling a drawer wants "how much change is in here", and two rows that net to
    // one number is an arithmetic step in the middle of a count.
    decimal FloatCash,
    decimal PayoutCash,
    // The cash reconciliation, appended strictly last and additively like the Payments* block above.
    //
    // ExpectedCashNow is the LIVE drawer figure — float plus cash from orders, less cash handed back
    // and less cash carried away — and it is a real field rather than something each screen subtracts
    // for itself: one definition, computed once where all four halves already exist. It MAY be
    // negative, because a cash refund taken after a collection leaves money the drawer no longer
    // holds; that is shown rather than prevented, and see CashLedger for the full case.
    //
    // Reconciliation is what was physically counted at the close, with the ledger figure it was
    // compared against FROZEN into it, so the comparison survives every refund taken afterwards.
    // IsReconciliationStale is the single boolean that says the frozen expectation and the live
    // figure have parted company — a refund against a closed shift moves the live figure and leaves
    // the snapshot alone, because the money physically left that drawer. There is no second money
    // column and no "expected" without a moment attached.
    decimal ExpectedCashNow,
    CashReconciliation? Reconciliation,
    bool IsReconciliationStale);

/// <summary>
/// The recorded cash count of one closed shift: what the ledger said the drawer held at the close
/// (<paramref name="ExpectedKopecks"/>, frozen and never moved afterwards), what was physically
/// counted, when it was RECORDED and, when the two differ, why.
/// <para>
/// The difference is DERIVED, not stored. A column for it would be a third copy of
/// <c>Counted − Expected</c>, and a third copy is a third thing to keep in step when one of the two
/// is written; there are four columns on the shift and none of them is the discrepancy.
/// </para>
/// </summary>
public sealed record CashReconciliation(
    long ExpectedKopecks,
    long CountedKopecks,
    DateTimeOffset CountedAt,
    string? Reason)
{
    /// <summary>Counted − Expected. Negative is a shortage, positive an overage.</summary>
    public long DiscrepancyKopecks => CountedKopecks - ExpectedKopecks;

    public CashDifference Difference => DiscrepancyKopecks switch
    {
        0 => CashDifference.Matched,
        < 0 => CashDifference.Shortage,
        _ => CashDifference.Overage
    };
}

/// <summary>
/// One line of the shift's product breakdown.
/// </summary>
/// <param name="ProductName">The name snapshot carried by the order line.</param>
/// <param name="ModifierName">The modifier/variant description, already composed by the query.</param>
/// <param name="Quantity">Units sold across the shift's completed orders.</param>
/// <param name="Revenue">Line revenue in major units.</param>
/// <param name="CategoryId">
/// The section the dish belongs to TODAY, or null when it has none. Read through the product, not
/// snapshotted onto the order line — see <see cref="IOrderReporting.GetProductAnalyticsAsync"/>.
/// </param>
/// <param name="CategoryName">That section's current name, or null.</param>
public sealed record ProductAnalyticsRowData(
    string ProductName,
    string ModifierName,
    int Quantity,
    decimal Revenue,
    Guid? CategoryId,
    string? CategoryName);

/// <summary>
/// One order line whose charged price differs from the price it was allowed to be sold at, with the
/// size of the difference.
/// </summary>
/// <param name="OrderNumber">Which order, in the form the operator reads on a receipt.</param>
/// <param name="Status">
/// Whether the order is a completed sale or was voided. Carried because the two must not be SUMMED
/// together: a voided line's money was refunded, so adding its discount to a real sale's would report
/// money that was never kept.
/// </param>
/// <param name="Quantity">Units on the line.</param>
/// <param name="ListPriceKopecks">
/// The allowed unit price, as the server states it: a bundle's own catalogue price, or the charged
/// price itself for a line with nothing to compare against.
/// </param>
/// <param name="PriceKopecks">The unit price actually charged.</param>
/// <param name="ReferenceTotalKopecks">
/// What the same dishes would have cost on their own, à la carte, for the WHOLE line — or null for a
/// line that is not a bundle. This is the second, independent signal: the first says "the price was
/// changed", this one says "this bundle was cheaper than its parts", and a bundle can be the second
/// without being the first. It is a NEGATIVE number for a bundle priced above its parts, which is a
/// surcharge and not a discount.
/// </param>
public sealed record DiscountedLine(
    int OrderNumber,
    OrderStatus Status,
    string ProductName,
    string? ModifierName,
    string? VariantName,
    int Quantity,
    long ListPriceKopecks,
    long PriceKopecks,
    long? ReferenceTotalKopecks)
{
    /// <summary>What the order lost on this line: (allowed − charged) × quantity. Negative if a price was RAISED.</summary>
    public long DiscountKopecks => (ListPriceKopecks - PriceKopecks) * Quantity;

    /// <summary>What was charged for the whole line, for comparison with <see cref="ReferenceTotalKopecks"/>.</summary>
    public long ChargedTotalKopecks => PriceKopecks * Quantity;

    /// <summary>Whether the line is a bundle, i.e. whether it has a composition at all.</summary>
    public bool IsBundle => ReferenceTotalKopecks.HasValue;

    /// <summary>
    /// How much less the bundle cost than its parts, for the whole line. Null for a line with no
    /// composition, and deliberately a DIFFERENT figure from <see cref="DiscountKopecks"/>: a bundle
    /// sold at its own price has nothing to report in the override column and may still be cheaper than
    /// its parts. NEGATIVE when the bundle was dearer than its parts — a surcharge, which is legal and
    /// must be labelled as one rather than as a negative discount.
    /// </summary>
    public long? BundleSavingKopecks => ReferenceTotalKopecks - ChargedTotalKopecks;
}
