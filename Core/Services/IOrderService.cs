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

    /// <summary>Closes the current shift and opens a new one. Fails when orders are still open.</summary>
    Task<Shift> CloseShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>All shift aggregates. Single source of truth for the shift report and analytics.</summary>
    Task<ShiftStats> GetShiftStatsAsync(Guid shiftId, CancellationToken cancellationToken = default);

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
    decimal RefundsTotal);

public sealed record ProductAnalyticsRowData(string ProductName, string ModifierName, int Quantity, decimal Revenue);
