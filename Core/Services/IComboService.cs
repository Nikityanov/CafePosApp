using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>One line's sale-side truth: the bundle's own price and the composition the server is willing to sell.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed record SaleComposition(long? PriceKopecks, IReadOnlyList<CheckoutComponent> Components);

/// <summary>Bundles: the catalogue half (full-replacement CRUD) and the sale half, which resolves a requested composition against what is actually on the menu right now.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public interface IComboService
{
    /// <summary>Bundles with their slots and the dishes behind them. Soft-deleted ones are excluded.</summary>
    Task<List<Combo>> GetCombosAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>One bundle with its slots, both products loaded, ordered as the catalogue shows them.</summary>
    Task<Combo?> GetComboAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Saves a bundle AND ITS WHOLE COMPONENT SET, including its price.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    Task SaveComboAsync(Combo combo, CancellationToken cancellationToken = default);

    /// <summary>Soft delete: the bundle leaves the catalogue, and bundles already sold stay on their receipts (their composition is a snapshot, not a reference).</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    Task DeleteComboAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>THE sale-side seam: takes the cart as the client asked for it and returns, index-aligned with <paramref name="lines"/> and in the same order, what the…</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    Task<IReadOnlyList<SaleComposition>> ResolveSaleCompositionsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);

    /// <summary>The catalogue's own price for each line, index-aligned, or null where the line is not a live bundle. NEVER REFUSES and never substitutes: a price lookup, not a sale check.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    Task<IReadOnlyList<long?>> ResolveSalePricesAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);

    /// <summary>The sale-side composition only — without the price.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    Task<IReadOnlyList<IReadOnlyList<CheckoutComponent>>> ResolveSaleComponentsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);
}
