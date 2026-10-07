namespace CafePos.Core.Models;

/// <summary>The composition of a parked cart line, held while the cart waits to be taken.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
