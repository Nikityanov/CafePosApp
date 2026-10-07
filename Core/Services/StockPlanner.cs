using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Ingredient shortage found while validating a write-off.</summary>
public sealed record StockShortage(Guid IngredientId, string IngredientName, string Unit, decimal Required, decimal Available);

/// <summary>What a cancellation could not put back on the shelf.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public sealed record StockReversal(IReadOnlyList<StockMovement> Movements, IReadOnlyList<string> IrreversibleIngredients)
{
    public bool IsPartial => IrreversibleIngredients.Count > 0;
}

/// <summary>Stock plan for a set of order lines: what has to be written off and whether it is available. One plan is used for both validation and the actual write-off, so both always agree.</summary>

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
                Kind = StockMovementKind.WriteOff,
                OrderId = order.Id,
                CreatedAt = now
            });
        }

        return movements;
    }

    /// <summary>
    /// Refuses a journal that no longer answers "how much did THIS order take off the shelf".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A positive row is a receipt, and in the app it carries no OrderId — <c>RestockAsync</c>
    /// leaves that null. An order carrying one means something was booked against it, and blind
    /// negation would hand back stock that never came off the shelf for this order.
    /// </para>
    /// <para>
    /// <b>Unknown counts, deliberately.</b> It is the kind of row written before
    /// <see cref="StockMovementKind"/> existed, and guessing which one it was is exactly what this
    /// refuses to do. The refusal still leaves the operator a way out: cancel without returning
    /// the stock.
    /// </para>
    /// </remarks>

    public static void EnsureJournalDescribesTheOrder(IReadOnlyList<StockMovement> journal, long orderNumber)
    {
        var receipt = journal.FirstOrDefault(movement =>
            movement.QuantityDelta > 0 &&
            movement.Kind is StockMovementKind.Delivery or StockMovementKind.Unknown);

        if (receipt is not null)
            throw new ConflictException(
                $"По заказу #{orderNumber} есть поступление на склад ({receipt.Reason}): остатки вернуть нельзя. Отмените заказ без возврата на склад.");
    }

    /// <summary>Applies a delta decided by <see cref="Common.StockDeltaPlan"/> and journals it.</summary>
    /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

    public static IReadOnlyList<StockMovement> WriteOffDeltas(
        StockDeltaPlan plan,
        IReadOnlyList<Ingredient> ingredients,
        Order order,
        DateTimeOffset now,
        ILogger logger)
    {
        var movements = new List<StockMovement>();
        foreach (var delta in plan.Deltas)
        {
            var ingredient = ingredients.FirstOrDefault(candidate => candidate.Id == delta.IngredientId);
            if (ingredient is null)
                // Unreachable through Decide, which only ever positions an ingredient that exists.
                // Cheaper to stay honest than to trust a caller.
                throw new ConflictException("Ингредиент не найден — остатки не изменены.");

            var before = ingredient.StockQuantity;
            var after = Math.Max(0, before + delta.QuantityDelta);
            ingredient.StockQuantity = after;

            logger.LogInformation(
                "Order #{OrderNumber} edited: {Ingredient} {Delta}, stock {Before} → {After}",
                order.OrderNumber, ingredient.Name, delta.QuantityDelta, before, after);

            movements.Add(new StockMovement
            {
                IngredientId = ingredient.Id,
                QuantityDelta = after - before,
                StockAfter = after,
                Reason = $"Правка заказа #{order.OrderNumber}",
                Kind = StockMovementKind.Edit,
                OrderId = order.Id,
                CreatedAt = now
            });
        }

        return movements;
    }

    /// <summary>Undoes `WriteOff` by reading the StockMovement journal of the order and applying the inverse delta.</summary>
    /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

    public static async Task<StockReversal> ReverseAsync(
        AppDbContext db,
        Order order,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        /// <summary>Belt and braces before negating anything.</summary>
        /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

        var journal = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.OrderId == order.Id)
            .ToListAsync(cancellationToken);

        EnsureJournalDescribesTheOrder(journal, order.OrderNumber);

        if (journal.Count == 0)
        {
            logger.LogInformation("Order #{OrderNumber} has no stock journal; nothing to return", order.OrderNumber);
            return new StockReversal([], []);
        }

        var netByIngredient = journal
            .GroupBy(movement => movement.IngredientId)
            .Select(group => new
            {
                IngredientId = group.Key,
                Net = group.Sum(movement => movement.QuantityDelta)
            })
            .Where(row => row.Net != 0)
            .ToList();

        // Tracked on purpose: the restored level has to be picked up as an UPDATE.
        var ingredients = await db.Ingredients
            .Where(ingredient => netByIngredient.Select(row => row.IngredientId).Contains(ingredient.Id))
            .ToListAsync(cancellationToken);

        var movements = new List<StockMovement>(netByIngredient.Count);
        var irreversible = new List<string>();
        foreach (var row in netByIngredient)
        {
            var ingredient = ingredients.FirstOrDefault(candidate => candidate.Id == row.IngredientId);
            if (ingredient is null)
            {
                /// <summary>StockMovements.IngredientId is ON DELETE CASCADE and ingredients are a HARD delete (they carry no IsDeleted flag, unlike Products), so deleting one de…</summary>
                /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

                irreversible.Add(row.IngredientId.ToString());
                logger.LogWarning(
                    "Order #{OrderNumber}: {Net} units of ingredient {IngredientId} cannot be returned to stock — it is no longer in the catalogue",
                    order.OrderNumber, -row.Net, row.IngredientId);
                continue;
            }

            var before = ingredient.StockQuantity;
            var after = Math.Max(0, before - row.Net);
            ingredient.StockQuantity = after;
            movements.Add(new StockMovement
            {
                IngredientId = ingredient.Id,
                QuantityDelta = after - before,
                StockAfter = after,
                Reason = $"Возврат по заказу #{order.OrderNumber}",
                Kind = StockMovementKind.Reversal,
                OrderId = order.Id,
                CreatedAt = now
            });
        }

        /// <summary>Through the DbSet, never order-by-hand into a loaded collection: the same trap PaymentRecorder's doc describes — an entity pushed into the collection…</summary>
        /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

        db.StockMovements.AddRange(movements);

        return new StockReversal(movements, irreversible);
    }
}

/// <summary>Builds stock plans from recipes. All quantity math stays in memory (see Ingredient).</summary>
internal static class StockPlanner
{
    /// <summary>What has to be written off for a cart, as dishes rather than as cart lines.</summary>
    /// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

    public static async Task<StockPlan> BuildAsync(
        AppDbContext db,
        IReadOnlyList<ProductDemand> demands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demands);

        var productIds = demands.Select(demand => demand.ProductId).Distinct().ToList();
        if (productIds.Count == 0) return new StockPlan(new Dictionary<Guid, decimal>(), [], []);

        var recipe = await db.RecipeItems.AsNoTracking()
            .Where(item => productIds.Contains(item.ProductId))
            .Select(item => new { item.ProductId, item.IngredientId, item.Quantity })
            .ToListAsync(cancellationToken);

        if (recipe.Count == 0) return new StockPlan(new Dictionary<Guid, decimal>(), [], []);

        var required = new Dictionary<Guid, decimal>();
        foreach (var demand in demands)
        {
            foreach (var item in recipe.Where(entry => entry.ProductId == demand.ProductId))
            {
                // "+=" and not "=": the same ingredient reached through two dishes, or through the same dish twice, is two withdrawals from the shelf.
                // Почему так — `docs/decisions/stock.md`

                required[item.IngredientId] = required.GetValueOrDefault(item.IngredientId) + item.Quantity * demand.Quantity;
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
