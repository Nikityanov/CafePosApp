using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed class InventoryService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<InventoryService> logger) : IInventoryService
{
    public async Task<List<Ingredient>> GetLowStockAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // SQLite keeps decimals as TEXT, so "StockQuantity <= MinStockLevel" would be a string
        // comparison in SQL (and "100" < "20" alphabetically). Ingredients are few, so the
        // comparison is done in memory instead.
        var ingredients = await db.Ingredients.AsNoTracking()
            .Where(ingredient => ingredient.IsAvailable)
            .ToListAsync(cancellationToken);

        return ingredients
            .Where(ingredient => ingredient.IsLowStock)
            .OrderBy(ingredient => ingredient.StockQuantity)
            .ToList();
    }

    public async Task<List<StockMovement>> GetMovementsAsync(Guid ingredientId, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var movements = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.IngredientId == ingredientId)
            .ToListAsync(cancellationToken);
        // SQLite cannot ORDER BY a DateTimeOffset column in SQL — sort in memory.
        return movements
            .OrderByDescending(movement => movement.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToList();
    }

    public async Task RestockAsync(Guid ingredientId, decimal quantity, string? comment = null, CancellationToken cancellationToken = default)
    {
        if (quantity == 0) throw new ValidationFailureException("Укажите количество больше нуля.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ingredient = await db.Ingredients.FirstOrDefaultAsync(item => item.Id == ingredientId, cancellationToken)
            ?? throw new EntityNotFoundException("Ингредиент не найден.");

        var now = timeProvider.GetUtcNow();
        ingredient.StockQuantity = Math.Max(0, ingredient.StockQuantity + quantity);
        db.StockMovements.Add(new StockMovement
        {
            IngredientId = ingredient.Id,
            QuantityDelta = quantity,
            StockAfter = ingredient.StockQuantity,
            Reason = string.IsNullOrWhiteSpace(comment) ? "Поставка" : comment.Trim(),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Restocked {Ingredient} by {Quantity}; new stock {Stock}", ingredient.Name, quantity, ingredient.StockQuantity);
    }

    /// <summary>
    /// Shortages for a cart, before it is sold.
    /// <para>
    /// The tuple shape is kept on PURPOSE, even though the planner behind it now takes expanded
    /// <c>ProductDemand</c>s. This is the method the menu screen calls while the customer is still
    /// standing there, and it has always answered for "the dishes on this cart". Bundles reach it the
    /// same way they reach the sale — through <c>ComboExpander</c> — so the preview and the write-off
    /// ask the planner about the same dishes and cannot disagree about what is missing.
    /// <para>
    /// The empty <c>Name</c> is not a placeholder that leaks: <c>BuildAsync</c> reads the identifier
    /// and the quantity, and a shortage is described from the INGREDIENT's own row, so there is no
    /// demand name on screen anywhere in that path.
    /// </para>
    /// </summary>
    public async Task<List<StockShortage>> PreviewShortagesAsync(IReadOnlyList<(Guid ProductId, int Quantity)> lines, CancellationToken cancellationToken = default)
    {
        var demands = lines
            .Select(line => new ProductDemand(line.ProductId, line.Quantity, Name: string.Empty))
            .ToList();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var plan = await StockPlanner.BuildAsync(db, demands, cancellationToken);
        return plan.Shortages.ToList();
    }
}
