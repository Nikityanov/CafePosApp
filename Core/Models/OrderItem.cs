using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid ProductId { get; set; }

    /// <summary>Product name snapshot, kept even if the product is renamed or deleted.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Unit price in kopecks at the moment of sale.</summary>
    public long PriceKopecks { get; set; }

    /// <summary>The price this line was ALLOWED to be sold at, in kopecks: the catalogue price before any manual review.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public long ListPriceKopecks { get; set; }

    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public string? SelectedModifierName { get; set; }
    public string? SelectedVariantName { get; set; }
    public int Quantity { get; set; }

    // Line total in kopecks (exact integer arithmetic, no rounding drift).
    [NotMapped]
    public long LineTotalKopecks => PriceKopecks * Quantity;

    [NotMapped]
    public decimal LineTotal => Money.FromKopecks(LineTotalKopecks);

    /// <summary>What this line was made of, for a bundle — one snapshot row per slot.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public List<OrderItemComponent> Components { get; set; } = new();
}
