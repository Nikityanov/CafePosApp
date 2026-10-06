using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Ingredient shortage found while validating a write-off.</summary>
public sealed record StockShortage(Guid IngredientId, string IngredientName, string Unit, decimal Required, decimal Available);

/// <summary>
/// What a cancellation could not put back on the shelf. The names (or, for an ingredient whose row
/// is gone together with its journal, the identifiers) the operator has to be told about — the
/// alternative is telling them the stock came back when it did not.
/// </summary>
public sealed record StockReversal(IReadOnlyList<StockMovement> Movements, IReadOnlyList<string> IrreversibleIngredients)
{
    public bool IsPartial => IrreversibleIngredients.Count > 0;
}

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

    /// <summary>
    /// Undoes <see cref="WriteOff"/> by reading the StockMovement journal of the order and applying
    /// the inverse delta. The mirror of the write-off, and deliberately NOT a recompute.
    /// <para>
    /// WHY a recompute would be a bug, not a simplification: a write-off is a recorded event, and only
    /// an event can be reversed. (1) UpdateOrderAsync lets the operator change the lines of an
    /// InProgress order and never touches stock, so order.Items no longer matches what was
    /// deducted — a recompute returns MORE than was ever written off (invented stock) or LESS if a
    /// line was removed, and in the second case the operator is told the shelf got its money back
    /// when it did not. (2) SaveRecipeItemAsync edits recipe quantities with no versioning, so the
    /// recipe at cancel time is not the recipe at checkout.
    /// </para>
    /// <para>
    /// Static and on the type rather than an instance method on a plan: it must NOT be given the
    /// plan's Required/Ingredients, because reading those is exactly the recompute that is wrong
    /// here. Nothing is saved — the caller's SaveChanges/Commit is the only commit point.
    /// </para>
    /// </summary>
    public static async Task<StockReversal> ReverseAsync(
        AppDbContext db,
        Order order,
        DateTimeOffset now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Belt and braces before negating anything. Nothing in the app writes a positive movement
        // with an OrderId today (a delivery has no order, RestockAsync leaves OrderId null), and a
        // write-off can only be clamped to zero, never flipped positive — but a future per-order
        // manual adjustment would also carry OrderId, and blind negation would silently undo that
        // too. Refusing the whole reversal keeps the shelf consistent: the caller's transaction
        // rolls back, and the operator can still cancel with LeaveWrittenOff.
        if (await db.StockMovements.AnyAsync(
                movement => movement.OrderId == order.Id && movement.QuantityDelta > 0, cancellationToken))
            throw new ConflictException(
                $"По заказу #{order.OrderNumber} есть поступление на склад: вернуть остатки на склад нельзя. Отмените заказ без возврата на склад.");

        var journal = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.OrderId == order.Id)
            .ToListAsync(cancellationToken);
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
                // StockMovements.IngredientId is ON DELETE CASCADE and ingredients are a HARD delete
                // (they carry no IsDeleted flag, unlike Products), so deleting one destroys its
                // journal rows: there is nothing left to invert and no name left to report — only
                // the identifier. CatalogService.DeleteIngredientAsync refuses to delete an
                // ingredient that has movements precisely so this state cannot be created from the
                // app; this branch is what legacy and hand-edited databases report. Silently
                // skipping would tell the operator the stock came back when it did not.
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
                OrderId = order.Id,
                CreatedAt = now
            });
        }

        // Through the DbSet, never order-by-hand into a loaded collection: the same trap
        // PaymentRecorder's doc describes — an entity pushed into the collection of a tracked parent
        // is tracked as Modified and EF then UPDATEs a row that does not exist yet.
        db.StockMovements.AddRange(movements);

        return new StockReversal(movements, irreversible);
    }
}

/// <summary>Builds stock plans from recipes. All quantity math stays in memory (see Ingredient).</summary>
internal static class StockPlanner
{
    /// <summary>
    /// What has to be written off for a cart, as dishes rather than as cart lines.
    /// <para>
    /// <b>THE INPUT IS EXPANDED, NOT A LIST OF LINES.</b> It used to be
    /// <c>(ProductId, Quantity)</c> pairs taken straight from the cart, which meant a bundle arrived
    /// here as itself — and a bundle has no recipe, because it is a rollup rather than a dish, so its
    /// ingredients were never looked up and never written off. Taking the expanded demands instead
    /// (see <c>ComboExpander</c>) is what lets a bundle's slots reach the same recipe lookup as any
    /// other dish, with no second write-off engine and no change to anything below this line.
    /// </para>
    /// <para>
    /// <c>ProductDemand</c> rather than a bare pair because the shortage message needs the dish's name,
    /// and a shortage a cashier cannot name is a shortage they have to go and look up.
    /// </para>
    /// <para>
    /// Duplicates are expected and are summed below: two lines of the same dish, or two slots of the
    /// same dish inside one bundle, are one ingredient demand and must add up rather than overwrite
    /// each other.
    /// </para>
    /// </summary>
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
                // "+=" and not "=": the same ingredient reached through two dishes, or through the same
                // dish twice, is two withdrawals from the shelf. Overwriting here would deduct one of
                // them and the stock would drift up by exactly the amount of the one that vanished.
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
