using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>One dish the stock planner has to write off, with how many and what it is called.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed record ProductDemand(Guid ProductId, int Quantity, string Name);

/// <summary>Turns cart lines into the flat (dish, how many) list the existing stock planner already consumes.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public static class ComboExpander
{
    /// <summary>Every dish the given cart lines need, one demand per line per slot. Not merged: the planner sums the quantities itself, and merging here would mean choosing one name among several for the same dish.</summary>

    public static List<ProductDemand> Expand(IEnumerable<CheckoutLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var demands = new List<ProductDemand>();
        foreach (var line in lines)
        {
            // A line with no slots is an ordinary dish and demands itself. This is the branch every
            // current cart takes, which is why nothing about the existing sale changes.
            if (line.Components is not { Count: > 0 } components)
            {
                demands.Add(new ProductDemand(line.ProductId, line.Quantity, line.ProductName));
                continue;
            }

            // A quantity of N on a bundle line is N COMPLETE bundles, so the slot's own multiplicity is
            // multiplied in rather than the line quantity replacing it.
            foreach (var component in components)
            {
                demands.Add(new ProductDemand(
                    component.ProductId,
                    component.QuantityPerUnit * line.Quantity,
                    component.ProductName));
            }
        }

        return demands;
    }

    /// <summary>Which bundle components have no recipe, so nothing will be written off for them.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static IReadOnlyList<string> DescribeComponentsWithoutRecipe(
        IEnumerable<CheckoutLine> lines,
        IReadOnlySet<Guid> productsWithRecipe)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(productsWithRecipe);

        // Distinct by dish: the same component in three lines is one thing the operator has to fix,
        // and a list that repeats it three times teaches them the message is noise.
        var withoutRecipe = new List<string>();
        var reported = new HashSet<Guid>();

        foreach (var line in lines)
        {
            if (line.Components is not { Count: > 0 } components) continue;

            foreach (var component in components)
            {
                if (productsWithRecipe.Contains(component.ProductId)) continue;
                if (!reported.Add(component.ProductId)) continue;

                withoutRecipe.Add($"{component.ProductName}: нет рецепта — по комбо ингредиенты не спишутся");
            }
        }

        return withoutRecipe;
    }
}
