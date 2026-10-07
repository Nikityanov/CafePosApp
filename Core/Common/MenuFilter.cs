using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>Which part of the menu the cashier is looking at: everything, one category, or bundles.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public static class MenuFilter
{
    /// <summary>Sort key of the «Все» chip.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static readonly Guid AllKey = Guid.Empty;

    /// <summary>Sort key of the «Комбо» chip.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static readonly Guid CombosKey = Guid.Parse("00000000-0000-0000-0000-0000000000c0");

    /// <summary>True for the bundles-only filter. Derived from the key rather than stored, so the chip strip and the product grid can never disagree about what is being shown.</summary>

    public static bool IsBundlesOnly(Guid? selectedKey) => selectedKey == CombosKey;

    /// <summary>The key that is actually usable, given one that was selected earlier and the keys that exist now.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static Guid Repair(Guid selectedKey, IReadOnlyCollection<Guid> knownKeys) =>
        knownKeys.Contains(selectedKey) ? selectedKey : AllKey;

    /// <summary>The dishes on show: the selected category's, and only those inside their time window.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

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
