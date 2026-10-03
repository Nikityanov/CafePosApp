using CafePos.Core.Models;

namespace CafePos.Core.Services;

public interface IOrderService
{
    Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<List<Order>> GetActiveOrdersAsync(CancellationToken cancellationToken = default);
    Task<List<Order>> GetCompletedOrdersAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift report's on-screen history: the orders of the shift that are closed, i.e. Completed
    /// AND Cancelled. Deliberately not <see cref="GetCompletedOrdersAsync"/> — cancelling flips a
    /// Completed order to Cancelled, so a manager would watch a voided sale disappear from the
    /// history instead of showing up marked as voided, and the one thing they most need to see is
    /// exactly the sale that went wrong.
    /// </summary>
    Task<List<Order>> GetShiftOrderHistoryAsync(Guid shiftId, CancellationToken cancellationToken = default);

    Task<List<Shift>> GetShiftsAsync(CancellationToken cancellationToken = default);
    Task<List<OrderStatusHistory>> GetStatusHistoryAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Moves the order to the next status and returns the updated order.</summary>
    Task<Order> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Voids a sale. A paid order is NOT refused: the money is refunded in full, mirrored back out
    /// of the methods it arrived in, in the same transaction that flips the status — the two guards
    /// this used to have ("the order is closed" and "the order is paid") are gone, because both of
    /// them left a real cashier with no way to give a customer their money back.
    /// <paramref name="stock"/> is the operator's one decision: leave the ingredients written off
    /// (made, handed over, or a discrepancy) or return them to the shelf.
    /// </summary>
    Task CancelOrderAsync(
        Guid orderId,
        string? reason = null,
        StockDisposition stock = StockDisposition.LeaveWrittenOff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a payment against an order: adds the ledger row and moves Orders.PaidKopecks in one
    /// save. <paramref name="amount"/> is rubles, clamped to the outstanding balance (a tendered
    /// surplus is change, not revenue) and rejected when nothing is owed.
    /// </summary>
    Task<Order> AddPaymentAsync(Guid orderId, decimal amount, PaymentMethod method, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns money on a finished order: adds the refund row(s) and lowers Orders.PaidKopecks in one
    /// save. Only for <see cref="OrderStatus.Completed"/> — money goes back after the goods left the
    /// bar. <paramref name="amount"/> is rubles, clamped to what is still collected.
    /// <para>
    /// There is NO payment method parameter, and that is the point: the refund mirrors the order's
    /// own payments, oldest first. A POS has two tills, and booking the refund under a method the
    /// money never arrived in would leave one of them wrong at the count with nothing in the app to
    /// say so.
    /// </para>
    /// </summary>
    Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>Payment ledger of one order, oldest first. Read through the DbSet, never through a collection.</summary>
    Task<List<OrderPayment>> GetOrderPaymentsAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the item list of an editable order (differential update, ids are kept).</summary>
    Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift currently open, or <c>null</c> when none is.
    /// </summary>
    /// <remarks>
    /// Null is a NORMAL state, not an error: a shift is opened deliberately now
    /// (<see cref="OpenShiftAsync"/>), so a terminal that has not been opened today, or was closed a
    /// moment ago, reports it as absent. It used to be an error — this call created a shift instead —
    /// which meant the opening float could never be recorded: the shift already existed by the time
    /// anybody was asked about the change in the drawer.
    /// </remarks>
    Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a shift and records the change put into the drawer to start it.
    /// </summary>
    /// <param name="floatKopecks">Change in the drawer at the opening, kopecks. May be 0 and may never
    /// be negative: a café whose change belongs to the owner opens with an explicit zero, which is a
    /// different and more useful fact than an absent float.</param>
    /// <param name="reason">Optional note on the float; see <see cref="ICashLedgerService.RecordFloatAsync"/>.</param>
    /// <remarks>
    /// REFUSES when a shift is already open. Two overlapping shifts would each have their own float,
    /// their own orders and their own count, and the drawer they describe would be the sum of two
    /// tills — the reconciliation would then balance against a figure nobody could have counted.
    /// <para>
    /// There is no float parameter that must be positive and no way to open a shift without saying
    /// what is in the drawer, because "I opened the till and counted it" is the one statement the
    /// end-of-shift count is later compared against.
    /// </para>
    /// </remarks>
    Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the previous shift's count left in the drawer, for pre-filling the opening float.
    /// </summary>
    /// <remarks>
    /// The COUNTED figure, not the expected one, because what carries over is what was physically
    /// there. Null when there is no earlier shift, or when the last one was closed without ever being
    /// counted — in which case there is nothing to suggest and the operator types it.
    /// </remarks>
    Task<long?> GetLastCountedCashKopecksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift a whole-archive export should describe: the open one, or the most recently closed
    /// when none is open.
    /// </summary>
    /// <remarks>
    /// Null only on a terminal that has never had a shift, which is a demo seed rather than a real
    /// state. Exists so the backup does not reach into the DbContext to decide which shift a report
    /// is about — that choice is a shift question and belongs with the shift rules.
    /// </remarks>
    Task<Shift?> GetLatestShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the active shift, recording the counted cash against the drawer figure the ledger holds
    /// at that moment, and returns the shift that was closed. Fails when orders are still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO NEXT SHIFT IS OPENED. It used to be, because a shift had to exist before anything could be
    /// sold; a shift is now opened on purpose with its change recorded, so closing leaves the terminal
    /// with no open shift and the opening screen is what comes next.
    /// </para>
    /// <para>
    /// Reconciling is MANDATORY: a shift cannot be closed without entering what was counted, and a
    /// mismatch has to carry a reason. <paramref name="countedCashKopecks"/> is kopecks, may be 0
    /// (an empty drawer is the case that matters most, not an absent count) and may never be
    /// negative. <paramref name="discrepancyReason"/> is required iff the count differs from the
    /// expectation, accepted but never required when it matches, and rejected rather than truncated
    /// when it is over-long — it is a mandatory audit field.
    /// </para>
    /// <para>
    /// The expectation is <c>CashLedger</c>'s figure: the opening float, plus the cash from orders,
    /// less the cash handed back and less what was carried away. A count taken against the payments
    /// alone would report a shortage of exactly the change that was in the till all day.
    /// </para>
    /// <para>
    /// There is NO shiftId parameter, deliberately: the service resolves the active shift itself.
    /// A caller naming it could reconcile one shift and have the service close another.
    /// </para>
    /// <para>
    /// A count, once recorded, is NOT re-editable — there is no path that rewrites these four
    /// columns. A silently overwritten count is worse than no count. Recounting, if it is ever
    /// wanted, needs an append-only <c>CashCounts(ShiftId, CountedAt, CashKopecks, Reason)</c>
    /// table and a report that reads both.
    /// </para>
    /// </summary>
    Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default);

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
}

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
/// snapshotted onto the order line — see <see cref="GetProductAnalyticsAsync"/>.
/// </param>
/// <param name="CategoryName">That section's current name, or null.</param>
public sealed record ProductAnalyticsRowData(
    string ProductName,
    string ModifierName,
    int Quantity,
    decimal Revenue,
    Guid? CategoryId,
    string? CategoryName);
