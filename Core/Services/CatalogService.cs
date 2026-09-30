using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed partial class CatalogService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<CatalogService> logger) : ICatalogService
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

    // ─── Categories ───

    public async Task SaveCategoryAsync(Category category, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category.Name)) throw new ValidationFailureException("Укажите название раздела.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Categories.FirstOrDefaultAsync(item => item.Id == category.Id, cancellationToken);
        if (existing is null) db.Categories.Add(category);
        else existing.Name = category.Name;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var category = await db.Categories.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (category is null) return;
        db.Categories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ─── Products ───

    public async Task SaveProductAsync(Product product, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(product.Name)) throw new ValidationFailureException("Укажите название товара.");
        if (product.PriceKopecks < 0) throw new ValidationFailureException("Цена не может быть отрицательной.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Products
            .Include(item => item.Variants)
            .FirstOrDefaultAsync(item => item.Id == product.Id, cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (existing is null)
        {
            db.Products.Add(product);
            db.PriceHistoryEntries.Add(CreateHistoryEntry(product.Id, 0, product.PriceKopecks, "Создание товара", now));
        }
        else
        {
            var oldPrice = existing.PriceKopecks;
            existing.Name = product.Name;
            existing.PriceKopecks = product.PriceKopecks;
            existing.CategoryId = product.CategoryId;
            existing.ModifierGroupId = product.ModifierGroupId;
            existing.HasVariants = product.HasVariants;
            existing.IsAvailable = product.IsAvailable;
            existing.PhotoPath = product.PhotoPath;
            existing.Allergens = product.Allergens;
            existing.Tags = product.Tags;
            existing.AvailableFromHour = product.AvailableFromHour;
            existing.AvailableToHour = product.AvailableToHour;

            SyncVariants(db, existing, product.Variants, now);

            if (oldPrice != product.PriceKopecks)
            {
                db.PriceHistoryEntries.Add(CreateHistoryEntry(product.Id, oldPrice, product.PriceKopecks, "Редактирование карточки", now));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (product is null) return;

        // Soft delete: the product leaves the menu but the order history stays consistent.
        product.IsDeleted = true;
        product.IsAvailable = false;
        product.DeletedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Product {ProductId} soft deleted", id);
    }

    public async Task RestoreProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new EntityNotFoundException("Товар не найден.");
        product.IsDeleted = false;
        product.DeletedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleProductAvailabilityAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(item => item.Id == productId, cancellationToken);
        if (product is null) return;
        product.IsAvailable = !product.IsAvailable;
        await db.SaveChangesAsync(cancellationToken);
    }


    /// <summary>
    /// Updates variants in place instead of deleting and recreating them: identifiers survive,
    /// so price history (and any future reference to a variant) stays valid.
    /// </summary>
    private void SyncVariants(AppDbContext db, Product existing, IReadOnlyCollection<ProductVariant> incoming, DateTimeOffset now)
    {
        var keptIds = new List<Guid>();

        foreach (var variant in incoming)
        {
            var variantId = variant.Id == Guid.Empty ? Guid.NewGuid() : variant.Id;
            var current = existing.Variants.FirstOrDefault(item => item.Id == variantId);

            if (current is null)
            {
                // Add through the DbSet, not through the loaded navigation: an entity pushed into
                // the Variants collection of a tracked product is tracked as Modified (its key is
                // already set, so EF cannot tell it is new), and EF then issues an UPDATE that
                // matches no row → DbUpdateConcurrencyException.
                db.ProductVariants.Add(new ProductVariant
                {
                    Id = variantId,
                    ProductId = existing.Id,
                    Name = variant.Name,
                    PriceKopecks = variant.PriceKopecks,
                    IsAvailable = variant.IsAvailable,
                    SortOrder = variant.SortOrder
                });
                keptIds.Add(variantId);
                continue;
            }

            if (current.PriceKopecks != variant.PriceKopecks)
            {
                db.PriceHistoryEntries.Add(CreateHistoryEntry(existing.Id, current.PriceKopecks, variant.PriceKopecks, $"Вариант «{variant.Name}»", now));
            }

            current.Name = variant.Name;
            current.PriceKopecks = variant.PriceKopecks;
            current.IsAvailable = variant.IsAvailable;
            current.SortOrder = variant.SortOrder;
            keptIds.Add(current.Id);
        }

        var removed = existing.Variants.Where(item => !keptIds.Contains(item.Id)).ToList();
        if (removed.Count > 0) db.ProductVariants.RemoveRange(removed);
    }

    private static PriceHistoryEntry CreateHistoryEntry(Guid productId, long oldKopecks, long newKopecks, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = productId,
        OldPriceKopecks = oldKopecks,
        NewPriceKopecks = newKopecks,
        ChangedAt = now,
        Reason = reason
    };


    // ─── Modifiers ───

    public async Task SaveModifierGroupAsync(ModifierGroup group, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(group.Name)) throw new ValidationFailureException("Укажите название группы.");
        if (group.Options.Count == 0) throw new ValidationFailureException("Добавьте хотя бы один вариант модификатора.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ModifierGroups
            .Include(item => item.Options)
            .FirstOrDefaultAsync(item => item.Id == group.Id, cancellationToken);

        if (existing is null)
        {
            db.ModifierGroups.Add(group);
        }
        else
        {
            existing.Name = group.Name;
            var keptIds = new List<Guid>();
            foreach (var option in group.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Name)) continue;

                var current = existing.Options.FirstOrDefault(item =>
                        (option.Id != Guid.Empty && item.Id == option.Id)
                        || string.Equals(item.Name, option.Name, StringComparison.OrdinalIgnoreCase));

                if (current is null)
                {
                    var newOption = new ModifierOption
                    {
                        Id = option.Id == Guid.Empty ? Guid.NewGuid() : option.Id,
                        Name = option.Name.Trim(),
                        ModifierGroupId = existing.Id,
                        IsAvailable = option.IsAvailable
                    };
                    existing.Options.Add(newOption);
                    keptIds.Add(newOption.Id);
                }
                else
                {
                    current.Name = option.Name.Trim();
                    keptIds.Add(current.Id);
                }
            }

            var removed = existing.Options.Where(item => !keptIds.Contains(item.Id)).ToList();
            if (removed.Count > 0) db.ModifierOptions.RemoveRange(removed);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteModifierGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var group = await db.ModifierGroups.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (group is null) return;
        db.ModifierGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleModifierOptionAvailabilityAsync(Guid optionId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var option = await db.ModifierOptions.FirstOrDefaultAsync(item => item.Id == optionId, cancellationToken);
        if (option is null) return;
        option.IsAvailable = !option.IsAvailable;
        await db.SaveChangesAsync(cancellationToken);
    }


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

    public async Task DeleteIngredientAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (ingredient is null) return;
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
