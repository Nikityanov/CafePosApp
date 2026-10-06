using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

public class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid ProductId { get; set; }

    /// <summary>Product name snapshot, kept even if the product is renamed or deleted.</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Unit price in kopecks at the moment of sale.</summary>
    public long PriceKopecks { get; set; }

    /// <summary>
    /// The price this line was ALLOWED to be sold at, in kopecks: the catalogue price before any
    /// manual review. Immutable once written.
    /// <para>
    /// For a bundle that is the bundle's OWN price (<see cref="Combo.PriceKopecks"/>) — the number
    /// accounting put in the card — and NOT the sum of its slots. A line added to an order whose
    /// template no longer exists has no catalogue price to be had, and is compared against its à la
    /// carte sum instead; that is the one weaker case, and it is why the figure is written once and
    /// never derived from the composition on read.
    /// </para>
    /// <para>
    /// <see cref="PriceKopecks"/> is what was actually taken and may be changed by hand;
    /// <c>PriceKopecks != ListPriceKopecks</c> therefore means "a price was overridden", for ANY line
    /// and not only for a bundle. That makes the shift report's discount section a detection control
    /// that costs the till nothing: the first override shows up in the same report as the shift,
    /// with no historical threshold to calibrate and no PIN path to bypass.
    /// </para>
    /// <para>
    /// The alternative was a separate "adjustments" log, and it is the weaker choice for a reason
    /// worth writing down: a check has to live where every change passes through. A log written by one
    /// branch is bypassed by the branch that writes the price instead (Square published exactly such a
    /// bypass), whereas a second column on the line is written by the same statement that writes the
    /// price.
    /// </para>
    /// <para>
    /// For bundles there is a second, independent signal:
    /// <c>Σ(Components.ReferencePriceKopecks)</c> — what the same dishes would have cost on their own.
    /// One signal is "the operator changed the price", the other is "this bundle was cheaper than its
    /// parts", and the report can show both without either having to be trusted alone. A bundle priced
    /// ABOVE its parts is a surcharge: the second signal goes negative, which is a fact about the
    /// bundle's pricing and not an override.
    /// </para>
    /// </summary>
    public long ListPriceKopecks { get; set; }

    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public string? SelectedModifierName { get; set; }
    public string? SelectedVariantName { get; set; }
    public int Quantity { get; set; }

    /// <summary>Line total in kopecks (exact integer arithmetic, no rounding drift).</summary>
    [NotMapped]
    public long LineTotalKopecks => PriceKopecks * Quantity;

    [NotMapped]
    public decimal LineTotal => Money.FromKopecks(LineTotalKopecks);

    /// <summary>
    /// What this line was made of, for a bundle — one snapshot row per slot. Empty for an ordinary
    /// dish, which is what makes "is this a combo" answerable without a flag.
    /// <para>
    /// DISPLAY ONLY: like <see cref="Order.Payments"/>, populate it with an explicit
    /// <c>Include</c> (or on a freshly added parent). Nothing in the money path reads it — the line
    /// total is <see cref="LineTotalKopecks"/> either way — and the order lists load items without
    /// their components, so an un-Included collection is empty rather than stale.
    /// </para>
    /// </summary>
    public List<OrderItemComponent> Components { get; set; } = new();
}
