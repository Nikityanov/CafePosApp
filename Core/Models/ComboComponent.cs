namespace CafePos.Core.Models;

/// <summary>
/// One slot of a <see cref="Combo"/>: a real product, how many of it per unit of the bundle, and
/// optionally what that slot is charged instead of the dish's own price.
/// </summary>
/// <remarks>
/// WHY THE SLOT HOLDS A <see cref="Product"/> AND NOT AN OPTION. A component has to be a real dish
/// with its own stock, its own 86 and its own line in the reports, and the component list on a sold
/// order is a snapshot of exactly that. This is the majority design (Square, Simphony, Lightspeed,
/// D365) and the alternative — options inside the bundle — is what D365 has to work around by
/// enumerating every combination, which is the combinatorial blow-up behind "you cannot copy groups"
/// in Simphony.
/// <para>
/// WHY THERE IS NO NESTING. No vendor in the surveyed set supports a bundle inside a bundle
/// (Shopify: "Nested bundles aren't supported"). A slot pointing at another combo would make the
/// price a recursive walk and the availability question unbounded, so a slot is always a dish.
/// </para>
/// </remarks>
public class ComboComponent
{
    public Guid Id { get; set; }
    public Guid ComboId { get; set; }
    public Combo Combo { get; set; } = null!;

    /// <summary>
    /// The dish in this slot. Restricted on delete: a dish that is part of a bundle must not
    /// disappear under it. Products are soft-deleted anyway, so this guard is for hand-edited
    /// databases and for the day a hard delete is ever introduced.
    /// </summary>
    public Guid ProductId { get; set; }

    /// <summary>
    /// Loaded when the catalogue screen reads the bundle. The product identity that survives into
    /// the order is a snapshot of name and price, not this reference.
    /// </summary>
    public Product? Product { get; set; }

    /// <summary>
    /// How many of this dish one unit of the bundle takes. Always at least 1.
    /// <para>
    /// The multiplicity lives HERE and is never derived from the cart line quantity: a quantity of 3
    /// on a combo line means three complete bundles (every vendor agrees), so the per-unit
    /// multiplier has to be a property of the slot or a second croissant would be impossible to
    /// express.
    /// </para>
    /// </summary>
    public int QuantityPerUnit { get; set; }

    /// <summary>
    /// What this slot is charged inside the bundle, in kopecks, or <c>null</c> to charge the dish's
    /// own price. The three states are deliberately distinct: <c>null</c> = the dish price, 0 = free,
    /// a number = this price instead.
    /// <para>
    /// Simphony's default is the same shape ("Add Side Prices To Meal Price"), and the price
    /// REPLACES the dish price rather than adding to it — Aloha's "Item price" method, and what
    /// <see cref="Common.ProductAddFlow.ResolvePrice"/> already does for a chosen variant.
    /// </para>
    /// </summary>
    public long? ComponentPriceKopecks { get; set; }

    /// <summary>
    /// What to sell instead when this dish is sold out, or <c>null</c> when there is nothing to
    /// fall back to.
    /// <para>
    /// Substitution rather than blocking the sale, as every serious vendor does (Simphony
    /// substitution groups, D365 product substitutes). D365 also re-prices the kit when the
    /// replacement is dearer, and that part is DELIBERATELY NOT IMPLEMENTED: a bundle's price is
    /// stated in its own card (<see cref="Combo.PriceKopecks"/>) and does not follow the parts, so the
    /// difference between the two dishes is absorbed by the café. The replacement is still recorded on
    /// the sold line's composition snapshot, at its own price, because that is what was served.
    /// </para>
    /// <para>
    /// With no substitute, the sale is refused naming the dish rather than quietly shipping something
    /// else.
    /// </para>
    /// </summary>
    public Guid? SubstituteProductId { get; set; }

    /// <summary>Loaded with <see cref="Product"/>. Nullable on purpose: most slots have none.</summary>
    public Product? SubstituteProduct { get; set; }
}