using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Food cost of a product and the price change history.</summary>
public sealed partial class CatalogService
{
    /// <summary>Food cost of a product, computed from its recipe (sum of quantity × unit cost).</summary>
    public async Task<decimal> CalculateCostPriceAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var recipeItems = await db.RecipeItems.AsNoTracking()
            .Include(item => item.Ingredient)
            .Where(item => item.ProductId == productId)
            .ToListAsync(cancellationToken);

        return Money.Round(recipeItems.Sum(item => item.Ingredient.CostPerUnit * item.Quantity));
    }

    public async Task<List<PriceHistoryEntry>> GetPriceHistoryAsync(Guid productId, int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entries = await db.PriceHistoryEntries.AsNoTracking()
            .Where(entry => entry.ProductId == productId)
            .ToListAsync(cancellationToken);
        // SQLite cannot ORDER BY a DateTimeOffset column in SQL — sort in memory.
        return entries
            .OrderByDescending(entry => entry.ChangedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
    }

    public async Task LogPriceChangeAsync(Guid productId, decimal oldPrice, decimal newPrice, string reason = "", CancellationToken cancellationToken = default)
    {
        if (Money.ToKopecks(oldPrice) == Money.ToKopecks(newPrice)) return;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.PriceHistoryEntries.Add(new PriceHistoryEntry
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            OldPrice = oldPrice,
            NewPrice = newPrice,
            ChangedAt = timeProvider.GetUtcNow(),
            Reason = reason
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
