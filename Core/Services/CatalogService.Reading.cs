using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// One half of the catalogue service: reading.
/// </summary>
/// <remarks>
/// The class is already partial and was already 416 lines with 24 methods over five aggregates.
/// Splitting it by aggregate is pure movement: no type changes, no namespace change, no change
/// to the primary constructor or to DI. Only this part declares the constructor and the
/// interface - the others repeat neither.
/// </remarks>
public sealed partial class CatalogService
{
    // ─── Reads ───

    public async Task<List<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Categories.AsNoTracking()
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Product>> GetProductsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Products.AsNoTracking()
            .Include(product => product.Category)
            .Include(product => product.ModifierGroup).ThenInclude(group => group!.Options)
            .Include(product => product.Variants)
            // Several collection includes would otherwise produce a cartesian row explosion.
            .AsSplitQuery()
            .Where(product => includeDeleted || !product.IsDeleted);

        return await query.OrderBy(product => product.Name).ToListAsync(cancellationToken);
    }

    public async Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Products.AsNoTracking()
            .Include(product => product.Category)
            .Include(product => product.ModifierGroup).ThenInclude(group => group!.Options)
            .Include(product => product.Variants)
            .AsSplitQuery()
            .FirstOrDefaultAsync(product => product.Id == id, cancellationToken);
    }

    public async Task<ModifierGroup?> GetModifierGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ModifierGroups.AsNoTracking()
            .Include(group => group.Options)
            .FirstOrDefaultAsync(group => group.Id == id, cancellationToken);
    }

    public async Task<Ingredient?> GetIngredientAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Ingredients.AsNoTracking().FirstOrDefaultAsync(ingredient => ingredient.Id == id, cancellationToken);
    }

    public async Task<List<ModifierGroup>> GetModifierGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var groups = await db.ModifierGroups.AsNoTracking()
            .Include(group => group.Options)
            .OrderBy(group => group.Name)
            .ToListAsync(cancellationToken);

        // AsNoTracking means EF does not fix up the inverse navigation, so every
        // ModifierOption.ModifierGroup stays null and the catalogue row renders an empty
        // "Группа: " label. GetProductsAsync does not hit this because it loads the group
        // from the product side. Set the back-reference explicitly — it is a pure in-memory
        // assignment on already-materialised entities.
        foreach (var group in groups)
            foreach (var option in group.Options)
                option.ModifierGroup = group;

        return groups;
    }

    public async Task<List<Ingredient>> GetIngredientsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ingredients = await db.Ingredients.AsNoTracking().ToListAsync(cancellationToken);

        // Sorting happens in memory: decimal quantities are stored as TEXT in SQLite.
        return ingredients.OrderBy(ingredient => ingredient.Name, StringComparer.CurrentCulture).ToList();
    }

    public async Task<List<RecipeItem>> GetRecipeItemsByProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.RecipeItems.AsNoTracking()
            .Include(item => item.Ingredient)
            .Where(item => item.ProductId == productId)
            .ToListAsync(cancellationToken);
    }

}
