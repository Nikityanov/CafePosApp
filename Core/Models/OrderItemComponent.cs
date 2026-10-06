namespace CafePos.Core.Models;

/// <summary>
/// What a combo line was actually made of at the moment it was sold: a snapshot of names and of both
/// prices, one row per slot.
/// </summary>
/// <remarks>
/// <b>WHY <see cref="ProductId"/> IS HERE BUT IS NOT A FOREIGN KEY.</b> The identifier is kept so a
/// later report can ask "how many bundles with this dish sold this month", but the rows must survive
/// the dish: this table is a record of a sale, and a sale does not stop having happened because the
/// dish was renamed or removed from the catalogue. Every figure the reports need — the name, the
/// price charged, the à la carte price — is stored on the row, which is why an old receipt can be
/// reprinted unchanged. See the same reasoning on <see cref="OrderItem.ProductName"/>.
/// <para>
/// <b>WHY BOTH PRICES ARE STORED.</b> <see cref="UnitPriceKopecks"/> is what this slot contributes to
/// the bundle's à la carte reference — the slot's own price, or the dish's when the catalogue states
/// none; <see cref="ReferencePriceKopecks"/> is what the same dish costs on its own. Simphony
/// keeps Price and Prep Cost side by side for exactly this reason: one transaction has to answer two
/// different reports — how many bundles sold, and what they would have cost separately. With only one
/// of the two, the discount on a bundle cannot be shown to the customer and cannot be reported on
/// later. Neither figure is what the customer was charged: that is
/// <see cref="OrderItem.PriceKopecks"/>, the bundle's own price. The second signal is also
/// independent of <see cref="OrderItem.ListPriceKopecks"/>: one is "the price was overridden", the
/// other is "this bundle was cheaper than its parts".
/// </para>
/// </remarks>
public class OrderItemComponent
{
    public Guid Id { get; set; }
    public Guid OrderItemId { get; set; }
    public OrderItem OrderItem { get; set; } = null!;

    /// <summary>
    /// Which dish this was. Deliberately not a foreign key — see the type remarks: the row outlives
    /// the catalogue entry.
    /// </summary>
    public Guid ProductId { get; set; }

    /// <summary>Dish name as it was written on the receipt, kept even if the dish is renamed.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>How many of this dish one unit of the bundle took, from <see cref="ComboComponent.QuantityPerUnit"/>.</summary>
    public int QuantityPerUnit { get; set; }

    /// <summary>
    /// What this slot is worth inside the bundle, in kopecks — Simphony's Price, and the figure the
    /// à la carte reference is built from. Free slots are 0, not null: null meant "the dish price"
    /// back in the catalogue, and on the sale it is already resolved. It is NOT what the customer was
    /// charged, and a substituted slot carries the substitute's own figure here.
    /// </summary>
    public long UnitPriceKopecks { get; set; }

    /// <summary>
    /// What the same dish costs à la carte, in kopecks — Simphony's Prep Cost. What the bundle
    /// "saved" is the difference between this and <see cref="UnitPriceKopecks"/>.
    /// </summary>
    public long ReferencePriceKopecks { get; set; }

    /// <summary>Slot order within the line, so a reprint shows the composition as it was entered.</summary>
    public int SortOrder { get; set; }
}