using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>Snapshot of the persisted cart.</summary>
public sealed record CartSnapshot(IReadOnlyList<CheckoutLine> Lines, DateTimeOffset? SavedAt)
{
    public static readonly CartSnapshot Empty = new([], null);
    public bool IsEmpty => Lines.Count == 0;
}

public interface IDraftOrderService
{
    /// <summary>Restores the cart that was autosaved before the app was closed.</summary>
    Task<CartSnapshot> LoadActiveCartAsync(CancellationToken cancellationToken = default);

    /// <summary>Autosaves the current cart (called on every change, debounced by the ViewModel).</summary>
    Task SaveActiveCartAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);

    Task ClearActiveCartAsync(CancellationToken cancellationToken = default);

    /// <summary>Parks the cart under a name so several checks can be kept open at once.</summary>
    Task<DraftOrder> ParkAsync(string? name, IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);

    Task<List<DraftOrder>> GetParkedAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a parked draft and removes it from the parking list.</summary>
    Task<CartSnapshot> TakeAsync(Guid draftId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default);
}
