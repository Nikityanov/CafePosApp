using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

public class Ingredient
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Unit { get; set; } = string.Empty; // г, кг, мл, л, шт

    /// <summary>Cost of one unit in kopecks (stored as INTEGER).</summary>
    public long CostPerUnitKopecks { get; set; }

    [NotMapped]
    public decimal CostPerUnit
    {
        get => Money.FromKopecks(CostPerUnitKopecks);
        set => CostPerUnitKopecks = Money.ToKopecks(value);
    }

    /// <summary>Fractional stock quantity. Kept as a decimal on SQLite (TEXT), therefore all comparisons/orderings on it are performed in memory — see InventoryService.</summary>

    public decimal StockQuantity { get; set; }

    /// <summary>Low-stock threshold in the same units as <see cref="StockQuantity"/>.</summary>
    public decimal MinStockLevel { get; set; }

    [MaxLength(200)]
    public string? Supplier { get; set; }

    public bool IsAvailable { get; set; } = true;

    // Navigation
    public List<RecipeItem> RecipeItems { get; set; } = new();

    // True when the stock is at or below the configured minimum.
    [NotMapped]
    public bool IsLowStock => IsAvailable && StockQuantity <= MinStockLevel;

    // ─── Display text ───
    // Composed on the model so the list binds a plain string instead of a MultiBinding over
    // two paths, which is markedly cheaper inside a CollectionView item template.

    // Stock with its unit, e.g. "250 г".
    [NotMapped]
    public string StockText => TextFormat.Quantity(StockQuantity, Unit);

    // `docs/decisions/schema.md`

    [NotMapped]
    public string CostPerUnitText => TextFormat.CostPerUnit(CostPerUnit, Unit);

    // Low-stock threshold with its unit, e.g. "мин: 200 г".
    [NotMapped]
    public string MinStockText => $"мин: {TextFormat.Quantity(MinStockLevel, Unit)}";
}
