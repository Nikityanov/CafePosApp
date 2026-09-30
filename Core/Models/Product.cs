using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Price in kopecks — what is actually stored in SQLite (INTEGER).</summary>
    public long PriceKopecks { get; set; }

    /// <summary>Price in rubles. Stored through <see cref="PriceKopecks"/>.</summary>
    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public bool IsAvailable { get; set; } = true;

    /// <summary>Soft delete: history and reports keep referring to the product.</summary>
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    public Guid? ModifierGroupId { get; set; }
    public ModifierGroup? ModifierGroup { get; set; }
    public bool HasVariants { get; set; }
    public List<ProductVariant> Variants { get; set; } = new();
    public List<RecipeItem> RecipeItems { get; set; } = new();
    public string? PhotoPath { get; set; }
    public string? Allergens { get; set; }
    public string? Tags { get; set; }
    public int? AvailableFromHour { get; set; }
    public int? AvailableToHour { get; set; }
}
