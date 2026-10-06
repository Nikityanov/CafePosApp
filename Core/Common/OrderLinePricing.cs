using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>
/// What a line was allowed to cost, what was actually charged, and whether the two disagree.
/// </summary>
/// <param name="ListPriceKopecks">
/// The allowed price, as the SERVER states it: a bundle's own <c>PriceKopecks</c>, and the charged
/// price itself for a line with nothing to compare against. Immutable for the life of the line.
/// </param>
/// <param name="PriceKopecks">What is actually charged — what <c>Order.RecalculateTotal</c> sums.</param>
/// <param name="IsOverridden">Whether the two differ. The one bit this whole feature exists to produce.</param>
public sealed record ResolvedLinePrice(long ListPriceKopecks, long PriceKopecks, bool IsOverridden);

/// <summary>
/// THE price comparison for an order line, and the only place in the app that performs it.
/// </summary>
/// <remarks>
/// <b>WHY ONE METHOD AND NOT ONE PER CALL SITE.</b> A Square user documented the bypass that shaped
/// this: a price check that lives on one branch is not a control at all, because a second branch that
/// writes the price never goes through it. Lightspeed ships a cashier with the power to discount to
/// 100%, Toast with "Any User", and Square has a published route where the PIN is simply not asked.
/// So the check is placed where EVERY price passes, and both callers — <see cref="CheckoutService"/>
/// and <see cref="OrderService"/> — come through this method. A second implementation of
/// <c>Price != ListPrice</c> would be a place where the control silently does not apply, which is the
/// exact failure the control exists to prevent.
/// <para>
/// <b>WHY THIS IS NOT A STANDARD, AND IS STILL WORTH HAVING.</b> PCI DSS v4.0.1 (397 pages) contains
/// zero occurrences of "discount", "price" or "pricing": no standard requires this. The argument is
/// margin — a median restaurant runs 2.8% net, and a 1% discount on sales eats roughly 36% of that —
/// and the fact that this state (any price, no PIN, no trace) sits below what the vendors ship. The
/// recomputation itself ("the components do not add up to the price") is our engineering idea and is
/// not published practice; the published analogue is the before/after review at Oracle, Bank of
/// America and Bitta. What makes it useful here is that it catches the FIRST occurrence with no
/// historical threshold to calibrate.
/// </para>
/// <para>
/// <b>WHY IT IS DETECTION AND NOT PREVENTION.</b> The charged price is not overwritten: a manual
/// review at the till is a legitimate thing for an operator to do, and a POS that refuses it is a POS
/// that trains people to work around it. So nothing is blocked and nothing is asked for at the till —
/// the manager sees the divergence in the shift report, after the fact, which is Tier-1 detection with
/// no friction at the counter. That is why there is no reason code either: a reason typed at the till
/// becomes the first value anyone ever picks, and only the aggregate analysis of the pattern is worth
/// anything. Who did it is deliberately absent too — that needs staff entities, sign-in, PIN and
/// permissions, a feature the size of this one, and the report says so rather than guessing.
/// </para>
/// <para>
/// <b>THE COMPARISON IS ONLY WORTH ANYTHING IF THE COMPONENTS ARE THE SERVER'S.</b> A client that
/// chose its own component list could always make the two agree. That is why the caller passes
/// components resolved against the catalogue (see <c>IComboService.ResolveSaleComponentsAsync</c>) and
/// never the ones the client sent: a fake composition cannot pretend to be the computed one, because
/// the computed one is not the client's to choose.
/// </para>
/// </remarks>
public static class OrderLinePricing
{
    /// <summary>
    /// The allowed price of one line, and the divergence from what is charged.
    /// </summary>
    /// <param name="components">
    /// The line's composition as resolved against the catalogue — empty or <c>null</c> for an
    /// ordinary dish.
    /// </param>
    /// <param name="chargedKopecks">The unit price actually charged, in kopecks.</param>
    /// <param name="bundlePriceKopecks">
    /// The bundle's OWN price, read from the catalogue by the caller
    /// (<c>IComboService.ResolveSaleCompositionsAsync</c> / <c>ResolveSalePricesAsync</c>). This is
    /// what the till was allowed to charge, and it is deliberately NOT computed here from the
    /// components: see the remarks.
    /// </param>
    /// <remarks>
    /// A line with no components has nothing to compare against, so its allowed price IS the charged
    /// price and it can never be reported as overridden. That is not a gap in the control: before
    /// bundles existed there was no second figure to compare, which is exactly why the column had to
    /// be backfilled as <c>ListPriceKopecks = PriceKopecks</c> — otherwise every historical line would
    /// show as a 100% discount. What a plain line can still be caught for is a LATER price edit, and
    /// that works because the allowed price is written once when the line is created and never
    /// rewritten afterwards.
    /// <para>
    /// <b>THE ALLOWED PRICE OF A BUNDLE IS THE BUNDLE'S OWN PRICE, NOT THE SUM OF ITS PARTS.</b> The
    /// sum is the à la carte reference and it is a different question, reported by the second signal
    /// <c>Σ(ReferencePriceKopecks × QuantityPerUnit)</c>. Recomputing the allowed price from the
    /// components would be the old model in one line: a bundle priced above its parts would be reported
    /// as a discount, a manual review of a correctly priced bundle would be reported as a discount
    /// against a number nobody allowed, and a dearer substitute would move the price again — the three
    /// exact things the reversal exists to stop.
    /// </para>
    /// <para>
    /// <b>THE ONE FALLBACK, AND WHY IT IS NOT A BUNDLE PRICE.</b> When a line has a composition but
    /// the catalogue has no price for it — a hand-built composition whose template is gone, edited
    /// onto an open order — the à la carte sum is used, because it is the only statement about such a
    /// line the SERVER can make. Dropping to the charged price instead would disable the control on
    /// exactly the path where a caller fabricates a composition most easily, and the shift report
    /// would show nothing. It is weaker by construction and the doc on
    /// <c>OrderItem.ListPriceKopecks</c> says so; it is not a second comparison, it is this one, in
    /// the one method every price passes through.
    /// </para>
    /// </remarks>
    public static ResolvedLinePrice Resolve(
        IReadOnlyList<CheckoutComponent> components,
        long chargedKopecks,
        long? bundlePriceKopecks = null)
    {
        if (components is not { Count: > 0 })
            return new ResolvedLinePrice(chargedKopecks, chargedKopecks, IsOverridden: false);

        // From the CALLER, and only from the caller: the price the catalogue states for the bundle,
        // never anything from the cart. When it is absent, the reference below is the fallback and
        // only the fallback.
        var listPriceKopecks = bundlePriceKopecks ?? ComboPricing.ReferenceKopecks(
            components.Select(component => new ComboComponentPrice(component.QuantityPerUnit, component.UnitPriceKopecks)));

        return new ResolvedLinePrice(listPriceKopecks, chargedKopecks, listPriceKopecks != chargedKopecks);
    }
}