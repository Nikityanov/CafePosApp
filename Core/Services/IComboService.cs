using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// One line's sale-side truth: the bundle's own price and the composition the server is willing to
/// sell.
/// </summary>
/// <param name="PriceKopecks">
/// What the till may charge for one unit — <c>Combo.PriceKopecks</c> from the catalogue — or
/// <c>null</c> for a line that is not a bundle.
/// </param>
/// <param name="Components">
/// The slots as the catalogue states them, with substitutes already applied. Empty for an ordinary
/// dish, which is what makes "is this a bundle" answerable without a flag.
/// </param>
/// <remarks>
/// The two figures together, because they answer two different questions and the caller needs both:
/// the price is what the customer pays, the composition is what the kitchen receives and what the
/// report measures the bundle against.
/// </remarks>
public sealed record SaleComposition(long? PriceKopecks, IReadOnlyList<CheckoutComponent> Components);

/// <summary>
/// Bundles: the catalogue half (full-replacement CRUD) and the sale half, which resolves a requested
/// composition against what is actually on the menu right now.
/// </summary>
/// <remarks>
/// The two halves live in one service because they answer one question — "what does this bundle
/// consist of, what may it be sold for, and may it be sold as it stands?" — and the sale half is the
/// only trustworthy source of the prices the price control compares against. A separate "sale
/// resolver" that read the catalogue its own way would be a second definition of a bundle, and two
/// definitions are how a bundle ends up priced one way on the menu and another way on the receipt.
/// </remarks>
public interface IComboService
{
    /// <summary>Bundles with their slots and the dishes behind them. Soft-deleted ones are excluded.</summary>
    Task<List<Combo>> GetCombosAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>One bundle with its slots, both products loaded, ordered as the catalogue shows them.</summary>
    Task<Combo?> GetComboAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a bundle AND ITS WHOLE COMPONENT SET, including its price. The write is a FULL
    /// REPLACEMENT: a slot missing from <paramref name="combo"/> is deleted.
    /// <para>
    /// This is not a convenience, it is the only safe shape. Square's upsert is full-replacement for
    /// the same reason — a partial write that only adds and updates silently DESTROYS the children it
    /// did not mention, so removing a component from a bundle would appear to do nothing and the bundle
    /// would keep selling the removed dish. Removing something from a form must remove it from the
    /// catalogue, or the operator will do it twice and then trust the screen even less.
    /// </para>
    /// <para>
    /// <c>PriceKopecks</c> is PERSISTED here and is never recomputed from the slots. It is the number
    /// accounting put in the card, and the sum of the slots is the à la carte reference it is measured
    /// against — see <see cref="Models.Combo"/> and <c>docs/PLAN-combos-order-details.md</c> §1.3.
    /// </para>
    /// </summary>
    /// <exception cref="Core.Errors.ValidationFailureException">
    /// No name, a price of zero or less, no slots, a slot pointing at a dish that is gone or
    /// soft-deleted, a multiplicity below 1, a slot price below zero, or a substitute that does not
    /// exist. Every one of them is a bundle that would sell for the wrong price or could not be sold at
    /// all.
    /// </exception>
    Task SaveComboAsync(Combo combo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft delete: the bundle leaves the catalogue, and bundles already sold stay on their receipts
    /// (their composition is a snapshot, not a reference). Hard delete would take the slots with it and
    /// leave nothing for an audit to read.
    /// </summary>
    Task DeleteComboAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// THE sale-side seam: takes the cart as the client asked for it and returns, index-aligned with
    /// <paramref name="lines"/> and in the same order, what the bundle costs and the composition the
    /// server is willing to sell.
    /// </summary>
    /// <remarks>
    /// The client's own component names and prices are DISCARDED and rebuilt from the catalogue.
    /// That is the whole reason this method exists and not a tidy-up: the price control in
    /// <see cref="Common.OrderLinePricing"/> compares the charged price with the bundle's own price
    /// and writes the composition onto a fiscal document, and both are worthless if the client gets to
    /// pick them — a forged composition, or a cart price of its own choosing, would defeat it exactly.
    /// So this is the only route by which a composition or an allowed price reaches a sale.
    /// <para>
    /// Substitution is applied HERE, and it moves the composition only: a dearer substitute is written
    /// into the snapshot with its own price, and the difference is the café's to absorb rather than the
    /// customer's. The café's decision about the price of a bundle is not re-opened by running out of
    /// an ingredient.
    /// </para>
    /// </remarks>
    /// <exception cref="Core.Errors.ValidationFailureException">
    /// A slot is unavailable and has no usable substitute, or the cart asks for a composition the
    /// catalogue no longer contains. Both name the dish, because "нельзя продать" without a name sends
    /// the operator hunting through the menu.
    /// </exception>
    Task<IReadOnlyList<SaleComposition>> ResolveSaleCompositionsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The catalogue's own price for each line, index-aligned, or <c>null</c> where the line is not a
    /// live bundle. NEVER REFUSES and never substitutes: a price lookup, not a sale check.
    /// </summary>
    /// <remarks>
    /// It exists for <c>OrderService.UpdateOrderAsync</c>, which adds a line to an order that is
    /// already open and therefore has no checkout transaction to refuse in — the editor is allowed to
    /// save whatever the order holds, including a composition whose template has since been deleted,
    /// and the honest answer there is "the catalogue has no price for this line", not an exception in
    /// the middle of a save. The sale path uses <see cref="ResolveSaleCompositionsAsync"/>, which is
    /// strict; both read the same rows through the same loader, so the two cannot drift apart on what
    /// a bundle costs.
    /// </remarks>
    Task<IReadOnlyList<long?>> ResolveSalePricesAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The sale-side composition only — <see cref="ResolveSaleCompositionsAsync"/> without the price.
    /// </summary>
    /// <remarks>
    /// Kept because the MAUI layer still calls it, and because a caller that only needs the slots has
    /// no business reading a price. <b>It says nothing about what a bundle costs</b>: the price is in
    /// <see cref="ResolveSaleCompositionsAsync"/>, and reading it off the composition's sum brings back
    /// the model the reversal removed.
    /// </remarks>
    Task<IReadOnlyList<IReadOnlyList<CheckoutComponent>>> ResolveSaleComponentsAsync(
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken = default);
}
