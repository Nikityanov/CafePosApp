namespace CafePos.Core.Common;

/// <summary>One slot of a bundle as the à la carte reference is counted: how many of a dish per unit, and what that one would cost on its own.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed record ComboComponentPrice(int QuantityPerUnit, long UnitKopecks)
{
    /// <summary>What this slot contributes to the à la carte reference of one unit of the bundle.</summary>
    public long LineKopecks => (long)QuantityPerUnit * UnitKopecks;
}

/// <summary>Where a bundle's own price stands against the à la carte cost of its parts.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public enum ComboPriceRelation
{
    /// <summary>The parts cost nothing, so there is no denominator: the percentage is undefined.</summary>
    Unknown = 0,

    /// <summary>Cheaper than its parts — the ordinary bundle, and the reason bundles exist.</summary>
    Cheaper,

    /// <summary>Exactly as much as its parts. Nothing to report.</summary>
    Equal,

    /// <summary>DEARER than its parts. Legal and intended: accounting prices the bundle, and a bundle may carry a surcharge. It must never be shown as a negative discount.</summary>

    Dearer
}

/// <summary>What a bundle costs — which is `Combo.PriceKopecks`, not a sum — and the à la carte reference it is measured against.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public static class ComboPricing
{
    /// <summary>The à la carte cost of one unit of the bundle: the sum of every slot, priced as the same dishes on their own.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static long ReferenceKopecks(IEnumerable<ComboComponentPrice> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Sum(component => component.LineKopecks);
    }

    /// <summary>The old name of `ReferenceKopecks`, kept so the MAUI layer keeps compiling while it is moved over.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static long TotalKopecks(IEnumerable<ComboComponentPrice> components) => ReferenceKopecks(components);

    /// <summary>How much cheaper (or dearer) the bundle is than its parts, in percent, rounded to two decimals.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static decimal? DiscountPercent(long referenceKopecks, long priceKopecks)
    {
        if (referenceKopecks <= 0) return null;

        var percent = (referenceKopecks - priceKopecks) * 100m / referenceKopecks;

        /// <summary>AwayFromZero, and not the banker's default: two decimals of a percentage is a rounding convention a reader will check by hand, and "5.005 rounds to 5.01" is the one that matches what a calculator shows.</summary>

        return Math.Round(percent, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Where the bundle's price stands against its parts, as a named state rather than a sign the caller has to interpret.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static ComboPriceRelation Compare(long referenceKopecks, long priceKopecks)
    {
        if (referenceKopecks <= 0) return ComboPriceRelation.Unknown;
        if (priceKopecks < referenceKopecks) return ComboPriceRelation.Cheaper;
        return priceKopecks > referenceKopecks
            ? ComboPriceRelation.Dearer
            : ComboPriceRelation.Equal;
    }

    /// <summary>What one slot contributes to the reference: the slot's own price when the catalogue states one, otherwise the dish price.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static long ResolveUnitKopecks(long? componentPriceKopecks, long productPriceKopecks) =>
        componentPriceKopecks ?? productPriceKopecks;
}
