namespace CafePos.Core.Models;

/// <summary>What a combo line was actually made of at the moment it was sold: a snapshot of names and of both prices, one row per slot.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public class OrderItemComponent
{
    public Guid Id { get; set; }
    public Guid OrderItemId { get; set; }
    public OrderItem OrderItem { get; set; } = null!;

    /// <summary>Which dish this was. Deliberately not a foreign key — see the type remarks: the row outlives the catalogue entry.</summary>

    public Guid ProductId { get; set; }

    /// <summary>Dish name as it was written on the receipt, kept even if the dish is renamed.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>How many of this dish one unit of the bundle took, from <see cref="ComboComponent.QuantityPerUnit"/>.</summary>
    public int QuantityPerUnit { get; set; }

    /// <summary>What this slot is worth inside the bundle, in kopecks — Simphony's Price, and the figure the à la carte reference is built from.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public long UnitPriceKopecks { get; set; }

    /// <summary>What the same dish costs à la carte, in kopecks — Simphony's Prep Cost. What the bundle "saved" is the difference between this and .</summary>

    public long ReferencePriceKopecks { get; set; }

    /// <summary>Slot order within the line, so a reprint shows the composition as it was entered.</summary>
    public int SortOrder { get; set; }
}
