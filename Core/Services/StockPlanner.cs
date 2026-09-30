using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Ingredient shortage found while validating a write-off.</summary>
public sealed record StockShortage(Guid IngredientId, string IngredientName, string Unit, decimal Required, decimal Available);

/// <summary>
/// Stock plan for a set of order lines: what has to be written off and whether it is available.
/// One plan is used for both validation and the actual write-off, so both always agree.
/// </summary>
internal sealed record StockPlan(
    IReadOnlyDictionary<Guid, decimal> Required,
    IReadOnlyList<Ingredient> Ingredients,
    IReadOnlyList<StockShortage> Shortages)
{
    public bool HasShortages => Shortages.Count > 0;

    public IReadOnlyList<string> DescribeShortages() => Shortages
        .Select(shortage => $"{shortage.IngredientName}: нужно {shortage.Required:0.##} {shortage.Unit}, есть {shortage.Available:0.##} {shortage.Unit}")
        .ToList();

    /// <summary>Applies the write-off to the tracked ingredients and returns the journal entries.</summary>
    public IReadOnlyList<StockMovement> WriteOff(Order order, DateTimeOffset now, ILogger logger)
    {
        var movements = new List<StockMovement>();
        foreach (var ingredient in Ingredients)
        {
            if (!Required.TryGetValue(ingredient.Id, out var quantity) || quantity == 0) continue;

            var before = ingredient.StockQuantity;
            var after = before - quantity;
            if (after < 0)
            {
                logger.LogWarning("Stock of {Ingredient} is insufficient: {Before} - {WriteOff}, clamped to zero",
                    ingredient.Name, before, quantity);
                after = 0;
            }

            ingredient.StockQuantity = after;
            movements.Add(new StockMovement
            {
                IngredientId = ingredient.Id,
                QuantityDelta = after - before,
                StockAfter = after,
                Reason = $"Заказ #{order.OrderNumber}",
                OrderId = order.Id,
                CreatedAt = now
            });
        }

        return movements;
    }
}

/// <summary>Builds stock plans from recipes. All quantity math stays in memory (see Ingredient).</summary>
internal static class StockPlanner
{
    public static async Task<StockPlan> BuildAsync(
        AppDbContext db,
        IReadOnlyList<(Guid ProductId, int Quantity)> lines,
        CancellationToken cancellationToken)
    {
        var productIds = lines.Select(line => line.ProductId).Distinct().ToList();
        if (productIds.Count == 0) return new StockPlan(new Dictionary<Guid, decimal>(), [], []);

        var recipe = await db.RecipeItems.AsNoTracking()
            .Where(item => productIds.Contains(item.ProductId))
            .Select(item => new { item.ProductId, item.IngredientId, item.Quantity })
            .ToListAsync(cancellationToken);

        if (recipe.Count == 0) return new StockPlan(new Dictionary<Guid, decimal>(), [], []);

        var required = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            foreach (var item in recipe.Where(entry => entry.ProductId == line.ProductId))
            {
                required[item.IngredientId] = required.GetValueOrDefault(item.IngredientId) + item.Quantity * line.Quantity;
            }
        }

        var ingredientIds = required.Keys.ToList();
        var ingredients = await db.Ingredients
            .Where(ingredient => ingredientIds.Contains(ingredient.Id))
            .ToListAsync(cancellationToken);

        var shortages = ingredients
            .Where(ingredient => ingredient.StockQuantity < required[ingredient.Id])
            .Select(ingredient => new StockShortage(ingredient.Id, ingredient.Name, ingredient.Unit, required[ingredient.Id], ingredient.StockQuantity))
            .ToList();

        return new StockPlan(required, ingredients, shortages);
    }
}
