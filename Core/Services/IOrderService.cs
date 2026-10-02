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

    Task<Shift> GetOrCreateActiveShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the active shift and opens a new one, recording the counted cash against the drawer
    /// figure the ledger holds at that moment. Fails when orders are still open.
    /// <para>
    /// Reconciling is MANDATORY: a shift cannot be closed without entering what was counted, and a
    /// mismatch has to carry a reason. <paramref name="countedCashKopecks"/> is kopecks, may be 0
    /// (an empty drawer is the case that matters most, not an absent count) and may never be
    /// negative. <paramref name="discrepancyReason"/> is required iff the count differs from the
    /// expectation, accepted but never required when it matches, and rejected rather than truncated
    /// when it is over-long — it is a mandatory audit field.
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
    // The cash reconciliation, appended strictly last and additively like the Payments* block above.
    //
    // ExpectedCashNow is the LIVE drawer figure, PaymentsCash − RefundsCash, and it is a real field
    // rather than something each screen subtracts for itself: one definition, computed once where
    // both halves already exist. Reconciliation is what was physically counted at the close, with
    // the ledger figure it was compared against FROZEN into it, so the comparison survives every
    // refund taken afterwards. IsReconciliationStale is the single boolean that says the frozen
    // expectation and the live figure have parted company — a refund against a closed shift moves
    // the live figure and leaves the snapshot alone, because the money physically left that
    // drawer. There is no second money column and no "expected" without a moment attached.
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
