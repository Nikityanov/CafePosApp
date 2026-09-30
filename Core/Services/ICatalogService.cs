using CafePos.Core.Models;

namespace CafePos.Core.Services;

public interface ICatalogService
{
    Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Products of the menu. Deleted (soft deleted) products are excluded by default.</summary>
    Task<List<Product>> GetProductsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ModifierGroup?> GetModifierGroupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Ingredient?> GetIngredientAsync(Guid id, CancellationToken cancellationToken = default);

    Task<List<ModifierGroup>> GetModifierGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Ingredients ordered by name (ordering happens in memory, see Ingredient).</summary>
    Task<List<Ingredient>> GetIngredientsAsync(CancellationToken cancellationToken = default);

    Task<List<RecipeItem>> GetRecipeItemsByProductAsync(Guid productId, CancellationToken cancellationToken = default);

    Task SaveCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveProductAsync(Product product, CancellationToken cancellationToken = default);

    /// <summary>Soft delete: the product disappears from the menu but history stays intact.</summary>
    Task DeleteProductAsync(Guid id, CancellationToken cancellationToken = default);

    Task RestoreProductAsync(Guid id, CancellationToken cancellationToken = default);

    Task ToggleProductAvailabilityAsync(Guid productId, CancellationToken cancellationToken = default);

    Task SaveModifierGroupAsync(ModifierGroup group, CancellationToken cancellationToken = default);
    Task DeleteModifierGroupAsync(Guid id, CancellationToken cancellationToken = default);
    Task ToggleModifierOptionAvailabilityAsync(Guid optionId, CancellationToken cancellationToken = default);

    Task SaveIngredientAsync(Ingredient ingredient, CancellationToken cancellationToken = default);
    Task DeleteIngredientAsync(Guid id, CancellationToken cancellationToken = default);
    Task ToggleIngredientAvailabilityAsync(Guid ingredientId, CancellationToken cancellationToken = default);

    Task SaveRecipeItemAsync(RecipeItem recipeItem, CancellationToken cancellationToken = default);
    Task DeleteRecipeItemAsync(Guid id, CancellationToken cancellationToken = default);

    Task<decimal> CalculateCostPriceAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<List<PriceHistoryEntry>> GetPriceHistoryAsync(Guid productId, int take = 20, CancellationToken cancellationToken = default);

    Task LogPriceChangeAsync(Guid productId, decimal oldPrice, decimal newPrice, string reason = "", CancellationToken cancellationToken = default);

    Task<string> ExportProductsCsvAsync(CancellationToken cancellationToken = default);

    Task<ProductImportResult> ImportProductsCsvAsync(string csvContent, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a catalogue import.</summary>
public sealed record ProductImportResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Warnings)
{
    public string Summary => $"Создано: {Created}, обновлено: {Updated}, пропущено: {Skipped}";
}
