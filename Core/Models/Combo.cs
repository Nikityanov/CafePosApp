namespace CafePos.Core.Models;

/// <summary>
/// A bundle in the catalogue: a template with a price of its own, <see cref="PriceKopecks"/>, and a
/// set of <see cref="Components"/> it is sold as.
/// </summary>
/// <remarks>
/// <b>THE PRICE IS A NUMBER ACCOUNTING PUT IN THE CARD, NOT A SUM THE TILL COMPUTES.</b> This was the
/// other way round first — a bundle had no price at all, the price was the sum of its slots, and
/// raising the price of a dish silently repriced every bundle containing it. The owner reversed that,
/// and the reason is ownership of the decision, not convenience: <b>accounting decides the price and
/// the discount, the till records it</b>. In Oracle Simphony this is the documented alternative to
/// "Add Side Prices To Meal Price" — the price is stated in the menu item definition and does not
/// follow the parts. A program that computes the price of a meal has taken the pricing decision away
/// from the person entitled to it, and no amount of documentation puts it back.
/// <para>
/// So the component sum survives, but under a different name and a different meaning: it is the
/// à la carte REFERENCE the bundle is measured against (see <c>ComboPricing.ReferenceKopecks</c> and
/// <c>ComboPricing.DiscountPercent</c>), and it never reaches the customer. Three consequences are
/// load-bearing, not side effects:
/// </para>
/// <list type="bullet">
/// <item>Changing a dish price does not reprice the bundles containing it, and a bundle MAY be dearer
/// than its parts — that is a surcharge, it is legal, and it is reported as one.</item>
/// <item>Substituting an unavailable component does not move the price. D365's "if the replacement is
/// dearer, the kit price is recalculated" is a rule of a model that computed the price from the
/// parts; under a fixed price the difference is the café's to absorb. What was actually served is
/// still written to the line's composition snapshot, because the kitchen and any later audit need to
/// know it.</item>
/// <item>A slot priced 0 no longer makes that part of the bundle free to the customer. It still
/// changes the reference, which is what a free slot is for: "the croissant in this bundle is on the
/// house" is a statement about the comparison, not about the charge.</item>
/// </list>
/// <para>
/// A combo is also not a <see cref="Product"/>: it has no stock of its own and no 86 — its slots do.
/// Square, Simphony, Lightspeed and D365 all draw the same line ("components are the truth, parent
/// is a rollup"), and an ingredient that belongs to two combos is written off once per sale of each,
/// which is why <see cref="ComboExpander"/> expands a combo before the planner sees it.
/// </para>
/// <para>
/// Soft delete for the same reason as <see cref="Product.IsDeleted"/>: sold combos stay on old
/// receipts, so removing one must not remove it from history.
/// </para>
/// </remarks>
public class Combo
{
    public Guid Id { get; set; }

    /// <summary>Catalogued name, matching the bound of <see cref="Product.Name"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What the till charges for one unit of this bundle, in kopecks.
    /// <para>
    /// Integer kopecks like every other price in this model, never rubles and never <c>decimal</c>:
    /// this number is compared with the price a line was charged and is the allowed side of the one
    /// price control in the app (<see cref="Common.OrderLinePricing"/>), and a rounding step inside
    /// that comparison is a kopeck of drift per line.
    /// </para>
    /// <para>
    /// NOT NULL and refused at save when it is zero or negative (see
    /// <c>IComboService.SaveComboAsync</c>): a sellable item at no price is a catalogue error, not a
    /// bargain, and a NULL here would have to become 0 at every read — a second, silent place where a
    /// missing price turns into a free meal.
    /// </para>
    /// </summary>
    public long PriceKopecks { get; set; }

    /// <summary>Soft delete: sold combos stay on historical receipts.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Order in the catalogue list. Ties are broken by name, so gaps are harmless.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The slots this bundle is made of. At least one, in practice: a bundle with no component is a
    /// card with nothing to sell.
    /// <para>
    /// They no longer determine the price — <see cref="PriceKopecks"/> does — but they are what the
    /// availability, the stock write-off and the à la carte reference are all computed from, so they
    /// are not decoration either.
    /// </para>
    /// </summary>
    public List<ComboComponent> Components { get; set; } = new();
}
