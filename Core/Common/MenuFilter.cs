using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>
/// Which part of the menu the cashier is looking at: everything, one category, or bundles.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE SENTINEL KEYS BELONG HERE, NOT ON THE CHIP.</b> "Everything" and "bundles only" are not
/// categories — a bundle carries no <c>CategoryId</c> — so the strip needs two keys that no generated
/// category id can equal. They lived on <c>CategoryMenuItemViewModel</c>, which made the rule "is this
/// a category that still exists?" depend on a row type in the presentation layer, and made it
/// unreachable from a test.
/// </para>
/// <para>
/// The two sentinels are <see cref="Guid.Empty"/> and a literal ending in <c>c0</c>, chosen so neither
/// can collide with a generated Guid. A <see cref="Category.Id"/> is a real <see cref="Guid"/>, so
/// "all" is deliberately not a Guid a database could produce rather than being one that cannot be.
/// </para>
/// </remarks>
public static class MenuFilter
{
    /// <summary>
    /// Sort key of the «Все» chip. <see cref="Guid.Empty"/> and not a random Guid, so it is stable
    /// across processes for the same reason as <see cref="CombosKey"/>. Real category ids are
    /// generated Guids, so nothing the database hands out can collide with it.
    /// </summary>
    public static readonly Guid AllKey = Guid.Empty;

    /// <summary>
    /// Sort key of the «Комбо» chip. A fixed literal, not a random Guid, so it is stable across
    /// processes — the strip is rebuilt and diffed on every return to the tab, and a key that changed
    /// per launch would make <c>SyncWith</c> treat the chip as a different item each time and lose the
    /// selection highlight.
    /// </summary>
    public static readonly Guid CombosKey = Guid.Parse("00000000-0000-0000-0000-0000000000c0");

    /// <summary>
    /// True for the bundles-only filter. Derived from the key rather than stored, so the chip strip and
    /// the product grid can never disagree about what is being shown.
    /// </summary>
    public static bool IsBundlesOnly(Guid? selectedKey) => selectedKey == CombosKey;

    /// <summary>
    /// The key that is actually usable, given one that was selected earlier and the keys that exist now.
    /// </summary>
    /// <remarks>
    /// <b>THE SELECTED CATEGORY MAY HAVE BEEN DELETED WHILE THE PAGE WAS CLOSED.</b> Shell re-runs the
    /// load on every return to the tab, so a chip can name a category that no longer exists. Falling
    /// back to «Все» is the only honest reading of that state: leaving the key pointing at a chip that
    /// is not there would highlight nothing and filter by nothing, and the grid would look empty for
    /// a reason the operator cannot see.
    /// <para>
    /// The two sentinels are always accepted, whether or not the caller built a chip for them, because
    /// they stand for "no category" rather than for a row.
    /// </para>
    /// </remarks>
    /// <param name="selectedKey">The key that was selected before the reload.</param>
    /// <param name="knownKeys">Every key the strip now offers, including the two sentinels.</param>
    public static Guid Repair(Guid selectedKey, IReadOnlyCollection<Guid> knownKeys) =>
        knownKeys.Contains(selectedKey) ? selectedKey : AllKey;

    /// <summary>
    /// The dishes on show: the selected category's, and only those inside their time window.
    /// </summary>
    /// <remarks>
    /// <b>THE BUNDLES FILTER EMPTIES THE LIST RATHER THAN NARROWING IT.</b> A bundle is not a
    /// <see cref="Product"/> and carries no category id, so every comparison against a category would be
    /// true at once and the grid would show the whole menu behind a chip that promises bundles. An empty
    /// grid with the bundle tiles on top of it is the honest answer.
    /// </remarks>
    /// <param name="products">Everything loaded, before filtering.</param>
    /// <param name="category">The chosen category, or <c>null</c> for everything.</param>
    /// <param name="localHour">
    /// The hour at the till, which decides the time window. Passed in because a dish served at 23:50 must
    /// not be judged by the hour on the server.
    /// </param>
    public static IReadOnlyList<Product> Apply(IEnumerable<Product> products, Category? category, int localHour)
    {
        if (category is null) return [.. products];
        if (localHour < 0 || localHour > 23) throw new ArgumentOutOfRangeException(nameof(localHour));

        return
        [
            .. products.Where(product =>
                product.CategoryId == category.Id
                && ProductAvailability.IsInTimeWindow(product.AvailableFromHour, product.AvailableToHour, localHour)),
        ];
    }
}