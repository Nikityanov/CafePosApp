namespace CafePos.Core.Common;

/// <summary>
/// One slot of a bundle as the à la carte reference is counted: how many of a dish per unit, and what
/// that one would cost on its own.
/// </summary>
/// <param name="QuantityPerUnit">How many of this dish one unit of the bundle takes. Always >= 1.</param>
/// <param name="UnitKopecks">
/// What one of them is charged inside the bundle, in kopecks — already resolved, because on the sale
/// side "the dish's own price" has stopped being an option.
/// </param>
/// <remarks>
/// Kopecks, never rubles: the sum is the à la carte cost of a cart line, and every other price in the
/// order is an integer count of kopecks. Converting to <see cref="decimal"/> here and back would be a
/// rounding step in the middle of the one calculation whose exactness is the reason the column is an
/// INTEGER.
/// <para>
/// What this sum is NOT, since that is the whole point of the reversal: it is not the price of the
/// bundle. <see cref="Models.Combo.PriceKopecks"/> is.
/// </para>
/// </remarks>
public sealed record ComboComponentPrice(int QuantityPerUnit, long UnitKopecks)
{
    /// <summary>What this slot contributes to the à la carte reference of one unit of the bundle.</summary>
    public long LineKopecks => (long)QuantityPerUnit * UnitKopecks;
}

/// <summary>
/// Where a bundle's own price stands against the à la carte cost of its parts.
/// </summary>
/// <remarks>
/// Three states and a fourth one, and the fourth is why this is an enum rather than a sign the caller
/// has to interpret: a bundle priced BELOW its parts is a discount, ABOVE them is a surcharge, equal is
/// neither, and a bundle whose parts add up to NOTHING cannot be compared at all. A signed percentage
/// covers the first three and cannot express the fourth, and a surcharge reported as a "−5% discount"
/// is exactly the dishonest label the plan forbids (с. 1.3: «наценка 5%», а не «скидка −5%»).
/// </remarks>
public enum ComboPriceRelation
{
    /// <summary>The parts cost nothing, so there is no denominator: the percentage is undefined.</summary>
    Unknown = 0,

    /// <summary>Cheaper than its parts — the ordinary bundle, and the reason bundles exist.</summary>
    Cheaper,

    /// <summary>Exactly as much as its parts. Nothing to report.</summary>
    Equal,

    /// <summary>
    /// DEARER than its parts. Legal and intended: accounting prices the bundle, and a bundle may carry
    /// a surcharge. It must never be shown as a negative discount.
    /// </summary>
    Dearer
}

/// <summary>
/// What a bundle costs — which is <see cref="Models.Combo.PriceKopecks"/>, not a sum — and the à la
/// carte reference it is measured against.
/// </summary>
/// <remarks>
/// <b>WHY NOTHING HERE COMPUTES A PRICE ANYMORE.</b> The sum of the slots used to BE the price, which
/// meant a dish price edit repriced every bundle containing it and nobody decided anything — the
/// program decided. The owner reversed it: the price is a number in the bundle's card, the sum is a
/// benchmark, and this class only counts the benchmark and compares against it. See
/// <c>docs/PLAN-combos-order-details.md</c> §1.3 and <see cref="Models.Combo"/>.
/// <para>
/// <b>WHAT THE REFERENCE IS STILL FOR.</b> It is what the report compares against — "was this bundle
/// cheaper than buying the parts?", a question the charged price alone cannot answer — and it is why
/// Simphony stores Price and Prep Cost side by side: both reports have to be buildable from one
/// transaction. The shift report's second, independent signal is
/// <c>Σ(ReferencePriceKopecks × QuantityPerUnit)</c> on the sold line, and this is where that figure
/// comes from at sale time.
/// </para>
/// <para>
/// <b>THE RESULT IS A CART LINE LIKE ANY OTHER</b>, which is why nothing in the order changed:
/// <c>Order.RecalculateTotal</c> stays <c>Σ(PriceKopecks × Quantity)</c> and a bundle of 3 is one
/// line of quantity 3, not three lines.
/// </para>
/// </remarks>
public static class ComboPricing
{
    /// <summary>
    /// The à la carte cost of one unit of the bundle: the sum of every slot, priced as the same dishes
    /// on their own. A REFERENCE, not a price — it never reaches the customer.
    /// <para>
    /// Quantity N on a cart line means N complete bundles, so the per-unit multiplicity stays in the
    /// slot and never enters this sum — that is the whole reason <see cref="ComboComponentPrice"/>
    /// carries it as a property of the slot.
    /// </para>
    /// </summary>
    public static long ReferenceKopecks(IEnumerable<ComboComponentPrice> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return components.Sum(component => component.LineKopecks);
    }

    /// <summary>
    /// The old name of <see cref="ReferenceKopecks"/>, kept so the MAUI layer keeps compiling while it
    /// is moved over.
    /// <para>
    /// <b>IT NO LONGER MEANS THE PRICE, AND NOBODY MAY READ IT AS ONE.</b> It is the same number, the
    /// same arithmetic, the same result — the à la carte reference. Every caller that used it to price a
    /// cart line is now pricing a cart line from the sum of its parts, which is precisely what
    /// <see cref="Models.Combo.PriceKopecks"/> exists to stop. Migrate to
    /// <see cref="ReferenceKopecks"/> and read the price off the bundle.
    /// </para>
    /// </summary>
    public static long TotalKopecks(IEnumerable<ComboComponentPrice> components) => ReferenceKopecks(components);

    /// <summary>
    /// How much cheaper (or dearer) the bundle is than its parts, in percent, rounded to two decimals.
    /// </summary>
    /// <param name="referenceKopecks">The à la carte sum, from <see cref="ReferenceKopecks"/>.</param>
    /// <param name="priceKopecks">What the bundle actually costs.</param>
    /// <returns>
    /// A POSITIVE number when the bundle is cheaper than its parts, a NEGATIVE one when it is dearer
    /// (a surcharge — legal, and never to be labelled a negative discount), exactly 0 when the two
    /// agree — and <c>null</c> when the reference is zero.
    /// </returns>
    /// <remarks>
    /// <b>THE ZERO REFERENCE RETURNS NULL, AND THIS IS THE POINT OF THE METHOD EXISTING.</b> There is no
    /// percentage of nothing: dividing by zero would be an exception at best and a fabricated figure at
    /// worst, and 0 is the worst of them all — a bundle priced 300 ₽ against free parts would be
    /// reported as "0% cheaper than its parts", which is a false statement about a bundle that costs
    /// the customer 300 ₽. A bundle with no reference has no percentage, and the caller shows
    /// nothing. <see cref="Compare"/> says the same thing without arithmetic.
    /// <para>
    /// Display only. The percentage is never fed back into money: the charge is
    /// <see cref="Models.Combo.PriceKopecks"/> in kopecks, and this is a rounded ratio of two kopeck
    /// figures for a human to read.
    /// </para>
    /// </remarks>
    public static decimal? DiscountPercent(long referenceKopecks, long priceKopecks)
    {
        if (referenceKopecks <= 0) return null;

        var percent = (referenceKopecks - priceKopecks) * 100m / referenceKopecks;

        // AwayFromZero, and not the banker's default: two decimals of a percentage is a rounding
        // convention a reader will check by hand, and "5.005 rounds to 5.01" is the one that matches
        // what a calculator shows.
        return Math.Round(percent, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Where the bundle's price stands against its parts, as a named state rather than a sign the
    /// caller has to interpret. A surcharge and a discount have to be tellable apart by the code that
    /// draws the line, not by whoever reads the number afterwards.
    /// </summary>
    public static ComboPriceRelation Compare(long referenceKopecks, long priceKopecks)
    {
        if (referenceKopecks <= 0) return ComboPriceRelation.Unknown;
        if (priceKopecks < referenceKopecks) return ComboPriceRelation.Cheaper;
        return priceKopecks > referenceKopecks
            ? ComboPriceRelation.Dearer
            : ComboPriceRelation.Equal;
    }

    /// <summary>
    /// What one slot contributes to the reference: the slot's own price when the catalogue states one,
    /// otherwise the dish price.
    /// <para>
    /// The three states of <see cref="Models.ComboComponent.ComponentPriceKopecks"/> stay distinct
    /// here, and collapsing any of them would change what a bundle means: null takes the dish price, 0
    /// is free, and a number is this price INSTEAD of the dish price rather than on top of it (Aloha's
    /// "Item price" method, and what <see cref="ProductAddFlow.ResolvePrice"/> already does for a
    /// chosen variant — Simphony's default, "Add Side Prices To Meal Price").
    /// </para>
    /// <para>
    /// A SLOT PRICE NO LONGER REACHES THE CUSTOMER. This value now counts towards
    /// <see cref="ReferenceKopecks"/> only; the charge is <see cref="Models.Combo.PriceKopecks"/>. A
    /// free slot still means "free of charge in this bundle" as a catalogue statement about the
    /// comparison, and nothing more.
    /// </para>
    /// </summary>
    public static long ResolveUnitKopecks(long? componentPriceKopecks, long productPriceKopecks) =>
        componentPriceKopecks ?? productPriceKopecks;
}
