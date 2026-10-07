using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>One ingredient as an order edit sees it: what the journal says the order took, what the recipe says it needs, and what is on the shelf.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public sealed record StockPosition(
    Guid IngredientId,
    string Name,
    string Unit,
    decimal Available,
    decimal Consumed,
    decimal RequiredBefore,
    decimal RequiredAfter);

/// <summary>A signed correction to one ingredient: negative takes it off the shelf, positive puts it back.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public sealed record StockPositionDelta(Guid IngredientId, decimal QuantityDelta);

/// <summary>The journal and today's recipe describe two different orders, so the difference between them cannot be applied.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public sealed record StockRecipeDrift(Guid IngredientId, string IngredientName, string Unit, decimal Consumed, decimal ByRecipe)
{
    /// <summary>Names the ingredient and both numbers, so the refusal is actionable rather than puzzling.</summary>

    public string Describe() =>
        $"{IngredientName}: по журналу списано {TextFormat.Quantity(Consumed, Unit)}, а по нынешнему рецепту заказ занимает {TextFormat.Quantity(ByRecipe, Unit)}";
}

/// <summary>
/// The decision an order edit makes about stock: what to move, what it cannot move for lack of
/// stock, and what it refuses to touch because the journal and the recipe disagree.
/// </summary>
/// <remarks>
/// <para>
/// A pure type, so the rules are testable without a database — the same bargain as
/// <see cref="BundlePlan"/>. It does no I/O and knows nothing about EF; the caller reads the
/// journal and the recipes and hands the numbers over.
/// </para>
/// <para>
/// <b>WHY THE OLD SIDE COMES FROM THE JOURNAL AND NOT FROM THE RECIPE.</b> Only a recorded event
/// can be corrected against a recorded event. Recipes are edited without versioning, so re-running
/// the recipe after the fact describes a different order than the one on the shelf.
/// </para>
/// </remarks>
public sealed record StockDeltaPlan(
    IReadOnlyList<StockPositionDelta> Deltas,
    IReadOnlyList<StockShortage> Shortages,
    IReadOnlyList<StockRecipeDrift> RecipeDrift)
{
    /// <summary>True when the whole edit may be applied. Anything left over means the shelf is not touched.</summary>

    public bool CanApply => Shortages.Count == 0 && RecipeDrift.Count == 0;

    /// <summary>True when the numbers cancel out — an edit that changes no dish changes no stock.</summary>

    public bool IsEmpty => Deltas.Count == 0;

    /// <summary>What the shortage says, in the wording the operator reads.</summary>

    public IReadOnlyList<string> DescribeShortages() =>
    [
        .. Shortages.Select(shortage =>
            $"{shortage.IngredientName}: нужно {TextFormat.Quantity(shortage.Required, shortage.Unit)}, есть {TextFormat.Quantity(shortage.Available, shortage.Unit)}")
    ];

    /// <summary>Decides the correction for one edit.</summary>
    /// <remarks>
    /// Drift is checked per ingredient and blocks the whole plan, because a delta computed on top of
    /// it would be a number nobody can justify. Shortages block it too, and the owner chose a
    /// refusal over a clamped write-off: a wrong stock level that looks right is worse than no
    /// edit at all.
    /// </remarks>

    public static StockDeltaPlan Decide(IReadOnlyList<StockPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var deltas = new List<StockPositionDelta>();
        var shortages = new List<StockShortage>();
        var drift = new List<StockRecipeDrift>();

        foreach (var position in positions)
        {
            // Non-zero means the recipe moved between the checkout and this edit. Reported rather
            // than absorbed, because absorbing it is what would invent stock.
            var mismatch = position.RequiredBefore - position.Consumed;
            if (mismatch != 0)
            {
                drift.Add(new StockRecipeDrift(
                    position.IngredientId, position.Name, position.Unit, position.Consumed, position.RequiredBefore));
                continue;
            }

            // The order took more than it should now hold, so the surplus goes back.
            var delta = position.Consumed - position.RequiredAfter;
            if (delta == 0) continue;

            if (delta < 0)
            {
                var needed = -delta;
                if (position.Available < needed)
                {
                    shortages.Add(new StockShortage(
                        position.IngredientId, position.Name, position.Unit, needed, position.Available));
                    continue;
                }
            }

            deltas.Add(new StockPositionDelta(position.IngredientId, delta));
        }

        return new StockDeltaPlan(deltas, shortages, drift);
    }
}
