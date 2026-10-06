using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>
/// A catalogue bundle read against what is on the shelf right now: what each slot may hold, what is in
/// it already, what the slots come to à la carte — or the dish that stops it being sold at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS DECIDES WHETHER A BUNDLE MAY BE SOLD, AND IT USED TO BE UNTESTABLE.</b> It was a private
/// nested record inside <c>MenuViewModel</c>, with three private helpers beside it, so nothing outside
/// the class could ask it a question. That is a poor place for the one rule that stands between a
/// cashier's tap and selling something the kitchen cannot make.
/// </para>
/// <para>
/// <b>WHY IT IS PURE, AND WHY THAT IS THE DESIGN.</b> It reads a <see cref="Combo"/> and its loaded
/// slots and returns a verdict. It never awaits a sheet and never touches the cart — the ViewModel
/// drives the UI and mutates the cart, exactly as it already does for an ordinary dish through
/// <see cref="ProductAddFlow"/>. One pattern for both kinds of add, so there is no second mechanic to
/// keep in step.
/// </para>
/// <para>
/// <b>SUBSTITUTION RATHER THAN BLOCKING.</b> The dish each slot offers is what would ACTUALLY be sold
/// for it — its own, or the declared substitute when its own has run out. Substitution rather than
/// blocking is what every serious vendor does (Simphony substitution groups, D365 product
/// substitutes). The authoritative answer is still <c>IComboService.ResolveSaleCompositionsAsync</c>,
/// called again on the tap: this is what the tile and the sheet are built from, not a second verdict
/// that could disagree.
/// </para>
/// <para>
/// <b>THE SUM IS THE À LA CARTE REFERENCE, NOT THE PRICE.</b> The bundle's own price is
/// <see cref="Combo.PriceKopecks"/>, read off the template by the caller. This sum is what the
/// discount is measured against — it never reaches the customer.
/// </para>
/// </remarks>
/// <param name="Options">The dishes a slot may hold, in catalogue order. Empty when blocked.</param>
/// <param name="Selection">The slots as they stand, in the same order. Empty when blocked.</param>
/// <param name="ReferenceKopecks">The à la carte sum of the slots. Zero when blocked.</param>
/// <param name="BlockedDishName">
/// The dish that cannot be sold, named; <c>null</c> when the bundle may be sold at all. A name rather
/// than a bool, because the cashier has to be told WHICH dish to replace in the catalogue.
/// </param>
public sealed record BundlePlan(
    IReadOnlyList<ComboSlotOption> Options,
    IReadOnlyList<ComboSlotChoice> Selection,
    long ReferenceKopecks,
    string? BlockedDishName)
{
    /// <summary>True when nothing blocks the sale.</summary>
    public bool CanBeSold => BlockedDishName is null;

    /// <summary>
    /// Reads a catalogue bundle against the stock as loaded.
    /// </summary>
    /// <remarks>
    /// Ordering goes through <see cref="ComboComposition.CatalogueOrder"/>, which owns the rule, so
    /// the tile's composition line, this sheet and <see cref="BundlePlan"/> cannot disagree about which
    /// dish comes first.
    /// </remarks>
    public static BundlePlan Describe(Combo template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var options = new List<ComboSlotOption>();
        var selection = new List<ComboSlotChoice>();
        var reference = 0L;

        foreach (var slot in ComboComposition.CatalogueOrder(template.Components))
        {
            var sold = SellableDish(slot);

            // The whole bundle is refused on the first unanswerable slot, not on the last: a sheet
            // offering two of three dishes and then refusing is worse than no sheet.
            if (sold is null)
                return new BundlePlan([], [], 0, SoldOutName(slot));

            // Unwrapped explicitly rather than through `sold.Product`: Nullable<ValueTuple<>> exposes no
            // members, so every read off it has to go through .Value.
            var (product, label) = sold.Value;
            var quantity = Math.Max(1, slot.QuantityPerUnit);

            // The dish's price, not the slot's stored override — see ComboFormViewModel.UnitKopecks.
            // `reference` is the «по отдельности» figure the discount is quoted from, so a stored price
            // the operator can no longer see or change must not feed it.
            var unit = product.PriceKopecks;

            options.Add(new ComboSlotOption(slot.ProductId, label, unit, product.PriceKopecks));
            selection.Add(new ComboSlotChoice(slot.ProductId, quantity));
            reference += (long)quantity * unit;
        }

        return new BundlePlan(options, selection, reference, null);
    }

    /// <summary>
    /// The dish a slot will actually be sold for, or <c>null</c> when neither it nor its substitute is
    /// available.
    /// </summary>
    /// <remarks>
    /// <b>PUBLIC, BECAUSE THIS RULE WAS WRITTEN TWICE.</b> The menu's bundle add and the order editor's
    /// composition edit each had a private copy of this method. They agreed by luck: the same
    /// predicate and the same «(замена: …)» label in both. One edit to one copy and the till would
    /// compose bundles one way and the order editor another, with nothing failing and nothing saying
    /// so. There is one copy now.
    /// </remarks>
    public static (Product Product, string Label)? SellableDish(ComboComponent slot)
    {
        if (IsSellable(slot.Product)) return (slot.Product!, slot.Product!.Name);

        if (IsSellable(slot.SubstituteProduct))
        {
            var substitute = slot.SubstituteProduct!;
            var replaced = slot.Product?.Name;

            // The label says what is being sold AND what it stands in for. A substitution is a decision
            // the operator never made, and a sheet that silently showed another dish's name would sell
            // something the cashier never looked at.
            return (substitute, replaced is null
                ? substitute.Name
                : $"{substitute.Name} (замена: {replaced})");
        }

        return null;
    }

    private static bool IsSellable(Product? product) => product is { IsAvailable: true, IsDeleted: false };

    /// <summary>
    /// The dish that blocks a bundle, named. Falls back to the identifier when the product row is gone
    /// — which a Restrict FK should make impossible from the app and a hand-edited database can still
    /// produce. An identifier in a refusal is read by nobody, but it is traceable to a catalogue row,
    /// where "a dish from the bundle" would not be.
    /// </summary>
    private static string SoldOutName(ComboComponent slot) => slot.Product?.Name ?? slot.ProductId.ToString();
}