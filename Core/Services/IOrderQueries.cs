using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// Reading orders. The order board's own port: it lists what is in flight and opens one.
/// </summary>
/// <remarks>
/// <para>
/// Split out of the former flat <c>IOrderService</c> because a screen that lists orders has no
/// business refunding one or closing the till, and an interface that offers both is an interface
/// that will eventually be used to do both.
/// </para>
/// <para>
/// Reads only. A caller that wants to change an order needs
/// <see cref="IOrderCommands"/>, <see cref="IOrderPayments"/> or <see cref="IShiftLedger"/>, and
/// the compiler — not a review — is what stops it.
/// </para>
/// </remarks>
public interface IOrderQueries
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

    Task<List<OrderStatusHistory>> GetStatusHistoryAsync(Guid orderId, CancellationToken cancellationToken = default);
}
