using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>Changing an order: its status, its lines, its contact details.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public interface IOrderCommands
{
    /// <summary>Moves the order to the next status and returns the updated order.</summary>
    Task<Order> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Voids a sale.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    Task CancelOrderAsync(
        Guid orderId,
        string? reason = null,
        StockDisposition stock = StockDisposition.LeaveWrittenOff,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the item list of an editable order (differential update, ids are kept).</summary>
    Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default);

    /// <summary>Records that somebody has opened this order, retiring the board's unread dot for it.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    Task MarkSeenAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Writes a phone and a promised time onto an order that has already been paid for — the customer thought of it after the money changed hands.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    Task<Order> AddContactDetailsAsync(
        Guid orderId,
        string? phone,
        DateTimeOffset? requestedAt,
        bool promoteToTakeaway = false,
        CancellationToken cancellationToken = default);
}
