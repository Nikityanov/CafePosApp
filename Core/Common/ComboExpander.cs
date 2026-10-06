using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>
/// One dish the stock planner has to write off, with how many and what it is called.
/// </summary>
/// <param name="ProductId">The dish.</param>
/// <param name="Quantity">How many of it, across every cart line that asked for it.</param>
/// <param name="Name">Its name, so a shortage can be reported the way the operator ordered it.</param>
public sealed record ProductDemand(Guid ProductId, int Quantity, string Name);

/// <summary>
/// Turns cart lines into the flat (dish, how many) list the existing stock planner already consumes.
/// </summary>
/// <remarks>
/// <b>WHY A BUNDLE IS EXPANDED RATHER THAN GIVEN ITS OWN WRITE-OFF ENGINE.</b> Toast Recipes and
/// Restaurant365 both cost a bundle analytically and neither deducts ingredients per component: the
/// real write-off has no precedent, and an engine built for it would be a second implementation of
/// recipe arithmetic, in a domain that already has one and already gets it right. So the bundle is
/// unrolled to the shape <c>StockPlanner.BuildAsync</c> takes, and every existing shortage check,
/// clamping and reversal keeps working without knowing that bundles exist.
/// <para>
/// The bundle itself contributes no demand. It is a rollup, not a dish with stock — a combo has no
/// recipe and no 86 of its own, and counting it as well would double the write-off the moment anybody
/// gave it one.
/// </para>
/// <para>
/// <b>A KNOWN LIMITATION, WRITTEN HERE SO IT IS NOT REDISCOVERED AS A BUG:</b> an ingredient that is
/// sold on its own AND is in two bundles is deducted correctly by the formula, but no invariant of
/// the form "how many complete bundles can be assembled right now" holds — Shopify's community
/// documents the same thing. Stating that now is cheaper than being asked why the shelf disagrees.
/// The fix is a separate task and a different question from the one this answers.
/// </para>
/// </remarks>
public static class ComboExpander
{
    /// <summary>
    /// Every dish the given cart lines need, one demand per line per slot. Not merged: the planner
    /// sums the quantities itself, and merging here would mean choosing one name among several for
    /// the same dish.
    /// </summary>
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

    /// <summary>
    /// Which bundle components have no recipe, so nothing will be written off for them. A WARNING and
    /// not a refusal, and the distinction matters: blocking the sale would turn a missing recipe row
    /// into a till that cannot sell coffee, while saying nothing leaves the shelf quietly diverging
    /// from the books — the quiet part is the damage. Toast keeps an 86 report for exactly this
    /// reason.
    /// <para>
    /// Ordinary dishes are not reported: one without a recipe has always been sellable in this app,
    /// and warning about them would bury the bundles inside a list of everything.
    /// </para>
    /// <para>
    /// Takes the set of dishes that DO have a recipe rather than reading the database, so this stays a
    /// pure function that can be reasoned about and tested on its own; the caller already has to load
    /// the ids for the planner's own query.
    /// </para>
    /// </summary>
    /// <param name="lines">The cart about to be sold.</param>
    /// <param name="productsWithRecipe">Ids of the dishes that have at least one recipe item.</param>
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