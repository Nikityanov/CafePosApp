using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>One half of the catalogue service: products.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed partial class CatalogService
{
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


    /// <summary>Updates variants in place instead of deleting and recreating them: identifiers survive, so price history (and any future reference to a variant) stays valid.</summary>

    private void SyncVariants(AppDbContext db, Product existing, IReadOnlyCollection<ProductVariant> incoming, DateTimeOffset now)
    {
        var keptIds = new List<Guid>();

        foreach (var variant in incoming)
        {
            var variantId = variant.Id == Guid.Empty ? Guid.NewGuid() : variant.Id;
            var current = existing.Variants.FirstOrDefault(item => item.Id == variantId);

            if (current is null)
            {
                /// <summary>Add through the DbSet, not through the loaded navigation: an entity pushed into the Variants collection of a tracked product is tracked as Modified (i…</summary>
                /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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


}
