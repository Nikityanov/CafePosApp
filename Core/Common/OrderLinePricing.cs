using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>What a line was allowed to cost, what was actually charged, and whether the two disagree.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

public sealed record ResolvedLinePrice(long ListPriceKopecks, long PriceKopecks, bool IsOverridden);

/// <summary>THE price comparison for an order line, and the only place in the app that performs it.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

public static class OrderLinePricing
{
    /// <summary>The allowed price of one line, and the divergence from what is charged.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    public static ResolvedLinePrice Resolve(
        IReadOnlyList<CheckoutComponent> components,
        long chargedKopecks,
        long? bundlePriceKopecks = null)
    {
        if (components is not { Count: > 0 })
            return new ResolvedLinePrice(chargedKopecks, chargedKopecks, IsOverridden: false);

        /// <summary>From the CALLER, and only from the caller: the price the catalogue states for the bundle, never anything from the cart. When it is absent, the reference below is the fallback and only the fallback.</summary>

        var listPriceKopecks = bundlePriceKopecks ?? ComboPricing.ReferenceKopecks(
            components.Select(component => new ComboComponentPrice(component.QuantityPerUnit, component.UnitPriceKopecks)));

        return new ResolvedLinePrice(listPriceKopecks, chargedKopecks, listPriceKopecks != chargedKopecks);
    }
}
