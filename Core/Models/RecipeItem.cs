using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// BOM/Recipe: links an Ingredient to a Product with a required quantity.
/// </summary>
public class RecipeItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public Guid IngredientId { get; set; }
    public Ingredient Ingredient { get; set; } = null!;

    /// <summary>
    /// How many units of the ingredient are needed for one product serving.
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal Quantity { get; set; }

    // Quantity with the ingredient's unit, e.g. "0,02 кг". Bound directly so the recipe list does not need a MultiBinding over and Ingredient.Unit.

    [NotMapped]
    public string QuantityText => TextFormat.Quantity(Quantity, Ingredient?.Unit);
}
