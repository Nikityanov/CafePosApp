using CafePos.Core.Models;

namespace CafePos.Core.Services;

public interface IOrderService
{
    Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    Task<List<Order>> GetActiveOrdersAsync(CancellationToken cancellationToken = default);
    Task<List<Order>> GetCompletedOrdersAsync(Guid shiftId, CancellationToken cancellationToken = default);
    Task<List<Shift>> GetShiftsAsync(CancellationToken cancellationToken = default);
    Task<List<OrderStatusHistory>> GetStatusHistoryAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Moves the order to the next status and returns the updated order.</summary>
    Task<Order> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(Guid orderId, string? reason = null, CancellationToken cancellationToken = default);

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
    string PeakHour);

public sealed record ProductAnalyticsRowData(string ProductName, string ModifierName, int Quantity, decimal Revenue);
