using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// Parked ("held") cart. Unsent carts are persisted so a crash or an accidental
/// page reload never destroys a half-finished order.
/// </summary>
public class DraftOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ShiftId { get; set; }

    /// <summary>True for the single autosaved cart of the current operator.</summary>
    public bool IsActiveCart { get; set; }
    public List<DraftOrderItem> Items { get; set; } = new();

    [NotMapped]
    public int TotalQuantity => Items.Sum(item => item.Quantity);

    [NotMapped]
    public decimal Total => Money.FromKopecks(Items.Sum(item => item.LineTotalKopecks));
}

/// <summary>A single line of a parked cart.</summary>
public class DraftOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftOrderId { get; set; }
    public DraftOrder DraftOrder { get; set; } = null!;
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public long PriceKopecks { get; set; }

    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public string? SelectedModifierName { get; set; }
    public string? SelectedVariantName { get; set; }
    public int Quantity { get; set; }

    [NotMapped]
    public long LineTotalKopecks => PriceKopecks * Quantity;
}
