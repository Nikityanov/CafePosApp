using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>One half of the catalogue service: ingredients.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed partial class CatalogService
{
    // ─── Ingredients & recipes ───

    public async Task SaveIngredientAsync(Ingredient ingredient, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ingredient.Name)) throw new ValidationFailureException("Укажите название ингредиента.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Ingredients.FirstOrDefaultAsync(item => item.Id == ingredient.Id, cancellationToken);
        if (existing is null)
        {
            db.Ingredients.Add(ingredient);
        }
        else
        {
            existing.Name = ingredient.Name;
            existing.Unit = ingredient.Unit;
            existing.CostPerUnitKopecks = ingredient.CostPerUnitKopecks;
            existing.StockQuantity = ingredient.StockQuantity;
            existing.MinStockLevel = ingredient.MinStockLevel;
            existing.Supplier = ingredient.Supplier;
            existing.IsAvailable = ingredient.IsAvailable;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Hard delete, with one exception that is not cosmetic: an ingredient that has ever been written off cannot be deleted.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public async Task DeleteIngredientAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ingredient is null) return;

        if (await db.StockMovements.AnyAsync(movement => movement.IngredientId == id, cancellationToken))
            throw new ConflictException(
                $"Нельзя удалить «{ingredient.Name}»: по нему есть записи склада. Отключите ингредиент вместо удаления, иначе возврат остатков на склад будет невозможен.");

        db.Ingredients.Remove(ingredient);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleIngredientAvailabilityAsync(Guid ingredientId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(item => item.Id == ingredientId, cancellationToken);
        if (ingredient is null) return;
        ingredient.IsAvailable = !ingredient.IsAvailable;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveRecipeItemAsync(RecipeItem recipeItem, CancellationToken cancellationToken = default)
    {
        if (recipeItem.Quantity <= 0) throw new ValidationFailureException("Количество ингредиента должно быть больше нуля.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.RecipeItems.FirstOrDefaultAsync(item => item.Id == recipeItem.Id, cancellationToken);
        if (existing is null) db.RecipeItems.Add(recipeItem);
        else existing.Quantity = recipeItem.Quantity;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteRecipeItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var item = await db.RecipeItems.FirstOrDefaultAsync(recipe => recipe.Id == id, cancellationToken);
        if (item is null) return;
        db.RecipeItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

}
