using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>Reading orders. The order board's own port: it lists what is in flight and opens one.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IOrderQueries
{
    Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<List<Order>> GetActiveOrdersAsync(CancellationToken cancellationToken = default);

    Task<List<Order>> GetCompletedOrdersAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>The shift report's on-screen history: the orders of the shift that are closed, i.e.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<List<Order>> GetShiftOrderHistoryAsync(Guid shiftId, CancellationToken cancellationToken = default);

    Task<List<OrderStatusHistory>> GetStatusHistoryAsync(Guid orderId, CancellationToken cancellationToken = default);
}
