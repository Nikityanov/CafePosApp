namespace CafePos.Core.Models;

/// <summary>
/// The composition of a parked cart line, held while the cart waits to be taken.
/// </summary>
/// <remarks>
/// A duplicate of <see cref="OrderItemComponent"/> on <see cref="DraftOrderItemId"/> rather than a
/// shared parent, because the two rows are written by different operations at different times: a
/// draft is rewritten wholesale on every cart autosave (see <c>DraftOrderService</c>, where old lines
/// are deleted and re-inserted), while an order item's snapshot is written once, inside the
/// checkout transaction, and never rewritten. Sharing a table would put those two lifetimes in one
/// cascade and make an autosave touch order history.
/// <para>
/// The columns mirror the order-side ones exactly, including both prices, so that taking a parked
/// cart and checking it out produces the same snapshot a cart that was never parked would — the
/// operator typed those prices once and must not lose them to a round trip through the parking lot.
/// </para>
/// </remarks>
public class DraftOrderItemComponent
{
    public Guid Id { get; set; }
    public Guid DraftOrderItemId { get; set; }
    public DraftOrderItem DraftOrderItem { get; set; } = null!;

    /// <summary>Which dish this was. Not a foreign key, exactly as on <see cref="OrderItemComponent"/>.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Dish name as it was on the cart when it was parked.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>How many of this dish one unit of the bundle takes.</summary>
    public int QuantityPerUnit { get; set; }

    /// <summary>What this slot is worth inside the bundle, in kopecks — its part of the à la carte reference.</summary>
    public long UnitPriceKopecks { get; set; }

    /// <summary>What the same dish costs à la carte, in kopecks.</summary>
    public long ReferencePriceKopecks { get; set; }

    /// <summary>Slot order within the line.</summary>
    public int SortOrder { get; set; }
}
