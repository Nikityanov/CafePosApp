using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// Per-product variant (e.g., "Маленькая", "Средняя", "Большая") with absolute price.
/// </summary>
public class ProductVariant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long PriceKopecks { get; set; }

    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public bool IsAvailable { get; set; } = true;
    public int SortOrder { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
}
