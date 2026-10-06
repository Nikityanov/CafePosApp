using CafePos.Core.Models;

namespace CafePos.Presentation.ViewModels;
/// <summary>
/// What a bundle tile prints about what is INSIDE the bundle: one line of dish names, cut to a budget
/// and closed with a count of what did not fit.
/// </summary>
/// <remarks>
/// <b>WHY THE TILE NEEDS THIS AT ALL.</b> A bundle printed only its name and its price — «coffe and
/// more — 350,00 ₽» — so the only way to learn what was in it was to open the composition sheet. That
/// answers no question a customer actually asks. «Что входит?» is asked at the counter, with the
/// customer standing there, and it is answered from the tile.
/// <para>
/// <b>ONE LINE, A BUDGET, AND A COUNT.</b> The tile has a fixed height and a fixed width, and a bundle
/// whose composition runs long must not grow it: «Эспрессо, Круассан, Чизкейк, Мин. вода» on a tile that
/// sized itself to it would push every other tile out of shape and make the strip unscannable, which is
/// the one thing a menu board may not be. So names are dropped from the end and the rest is named
/// outright — «Эспрессо, Круассан, и ещё 2» — rather than silently clipped mid-word. The budget is in
/// CHARACTERS and not in names, because a bundle of «Чай» and a bundle of «Капучино с корицей» are not
/// the same width and a count-based rule would fit two of the first and half of the second.
/// </para>
/// <para>
/// <b>NO PRICES, AND NO MULTIPLIER UNLESS IT IS NOT 1.</b> The operator naming a dish needs identity,
/// not arithmetic: the bundle's price is already on the tile and the per-dish price is in the sheet,
/// and a tile that repeated the arithmetic would be a receipt, not a menu. The multiplicity is
/// different — «2 × Круассан» and «Круассан» are different bundles — so it is printed when it is above
/// 1 and omitted otherwise.
/// </para>
/// <para>
/// <b>THE SLOT'S OWN DISH, NOT WHAT WILL ACTUALLY BE SOLD FOR IT.</b> A slot whose dish has run out is
/// being sold as its declared substitute, and the editor sheet names that substitution because the
/// cashier has to decide about it. The tile is not where that decision is made, the substituted name
/// is twice as long, and what a customer standing at the till is asking about is what the bundle
/// <i>is</i>. The tile also already carries the stock state that does change the answer — it dims and
/// names the dish outright when the bundle cannot be sold at all.
/// </para>
/// <para>
/// The whole thing is one pass over the components the tile was built from. <c>GetCombosAsync</c>
/// already loads every slot with its <see cref="Product"/> attached (see <c>ComboService.LoadAsync</c>),
/// so this costs no query at all — the names were on hand to compute the price and are now read twice.
/// </para>
/// </remarks>
internal static class ComboComposition
{
    /// <summary>
    /// How many characters the tile's composition line may use. GONE WITH THE 208dp TILE.
    /// <para>
    /// The tile is full width and wraps now, so there is no budget to keep and no «и ещё N» tail to
    /// measure. A character budget here existed only because a half-width card had to fit the
    /// composition on one line, and it produced exactly the wrong thing: a bundle the cashier could
    /// not read.
    /// </para>
    /// </summary>
    [Obsolete("The combo tile is full width and wraps; there is no character budget. Kept only to name what was removed and why.", false)]
    internal const int TileCharBudget = 24;

    /// <summary>
    /// The slots in the order a bundle reads in.
    /// </summary>
    /// <remarks>
    /// Ordered HERE and not off the loaded collection, because <c>ComboComponent</c> carries no
    /// <c>SortOrder</c> and the query behind it applies no <c>ORDER BY</c>: the collection's order is
    /// whatever the join produced. This is the one place the order is defined, and both the tile and
    /// the composition sheet go through it, so the two cannot disagree about what the first dish in a
    /// bundle is.
    /// </remarks>
    internal static IOrderedEnumerable<ComboComponent> CatalogueOrder(IEnumerable<ComboComponent> components) =>
        components
            .OrderBy(component => component.Product?.Name, StringComparer.CurrentCulture)
            .ThenBy(component => component.ProductId);

    /// <summary>The slots as the tile prints them: every name, in catalogue order.</summary>
    /// <remarks>
    /// NO LONGER TRUNCATED, and that is the owner's decision. The tile used to be a 208dp card in a
    /// horizontal strip, which forced a character budget and printed «Американо, и ещё 2» — a bundle
    /// whose contents the operator could not read, on the one screen whose purpose is naming dishes to
    /// a customer standing at the counter. The tile is full width now and wraps, so every component is
    /// printed. A hidden dish is a worse defect than a taller page.
    /// </remarks>
    internal static string Summarise(IEnumerable<ComboComponent> components)
    {
        var names = CatalogueOrder(components)
            .Select(slot => SlotName(slot.Product?.Name ?? slot.ProductId.ToString(), slot.QuantityPerUnit))
            .ToList();

        return string.Join(", ", names);
    }

    /// <summary>«Круассан», or «2 × Круассан» when the slot takes more than one.</summary>
    private static string SlotName(string name, int quantityPerUnit) =>
        quantityPerUnit > 1 ? $"{quantityPerUnit} × {name}" : name;
}
