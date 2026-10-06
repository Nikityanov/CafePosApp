using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// One component of a bundle, as the cart holds it: what goes into it, how many, and both prices.
/// </summary>
/// <remarks>
/// A snapshot, not a reference. The checkout writes these onto
/// <see cref="Models.OrderItemComponent"/> unchanged, so the name on a reprint is the name that was
/// on the receipt and the reference price is the one the discount was measured against.
/// <para>
/// Kopecks, unlike <see cref="CheckoutLine.Price"/> which speaks rubles: these two figures are written
/// straight into a line and compared against each other to decide whether a bundle was cheaper than
/// its parts, and a ruble round trip in the middle of that comparison would be a rounding step whose
/// only possible effect is to make a bundle look discounted when it is not.
/// </para>
/// </remarks>
public sealed record CheckoutComponent(
    Guid ProductId,
    string ProductName,
    int QuantityPerUnit,
    long UnitPriceKopecks,
    long ReferencePriceKopecks);

/// <summary>One cart line passed to the checkout.</summary>
public sealed record CheckoutLine(
    Guid ProductId,
    string ProductName,
    decimal Price,
    int Quantity,
    string? ModifierName = null,
    string? VariantName = null,
    /// <summary>
    /// The slots of a bundle, empty or <c>null</c> for an ordinary dish. A bundle is still ONE line
    /// with a quantity: N means N complete bundles.
    /// <para>
    /// These slots are NOT the price. <see cref="Price"/> is the amount charged, and for a bundle it
    /// is expected to be the bundle's own price (<c>Combo.PriceKopecks</c>); the sum of the slots is
    /// only the à la carte reference (<c>ComboPricing.ReferenceKopecks</c>), which the server
    /// recomputes and which never reaches the customer. The server rebuilds both figures from the
    /// catalogue regardless of what the cart sent — see
    /// <c>IComboService.ResolveSaleCompositionsAsync</c>.
    /// </para>
    /// </summary>
    IReadOnlyList<CheckoutComponent>? Components = null);

/// <summary>
/// What the customer hands over at the counter. <see cref="Amount"/> is rubles — the service/DTO
/// boundary speaks rubles and the entity speaks kopecks — and it is clamped to the order total
/// inside the checkout transaction: for a 1000 ₽ note on a 660 ₽ order the recorded payment is
/// 660 ₽ and the 340 ₽ change is the UI's business. There is no till/petty-cash model that could
/// absorb a recorded surplus.
/// </summary>
public sealed record PaymentIntent(decimal Amount, PaymentMethod Method);

/// <summary>
/// The order-level facts the till collected: how it is fulfilled, and the contact and the requested
/// time that go with that.
/// </summary>
/// <param name="OrderType">
/// Whether the customer eats here or takes it away. DECIDES WHETHER THE PHONE IS KEPT — see
/// <see cref="Order.CustomerPhone"/>.
/// </param>
/// <param name="CustomerPhone">Whatever was typed. Normalised here, and DROPPED for counter service.</param>
/// <param name="RequestedAt">What the customer asked for, or <c>null</c> for as soon as possible.</param>
/// <remarks>
/// A separate parameter rather than three optionals on the line list, because these are facts about
/// the ORDER, not about any line: a customer either takes the whole thing away or eats all of it on
/// the premises. Carrying them per line would let one order be half takeaway.
/// <para>
/// The phone is accepted as free text and normalised at the service boundary, never stored as typed.
/// E.164 is the storage and transmission format, E.123 the display one; normalising now is cheap and
/// normalising later is a migration over every row that already holds free text.
/// </para>
/// <para>
/// A phone in this record is NOT a subscription of any kind: Square is explicit that typing a number
/// does not sign the customer up for SMS, and this app sends no messages at all. The field records a
/// contact for one order and nothing else.
/// </para>
/// </remarks>
public sealed record OrderDetailsIntent(
    OrderType OrderType = OrderType.CounterService,
    string? CustomerPhone = null,
    DateTimeOffset? RequestedAt = null)
{
    /// <summary>
    /// What every caller that says nothing gets: eaten here, no contact, as soon as possible. This is
    /// the overwhelmingly common sale and it is also the one that stores the least personal data.
    /// </summary>
    public static readonly OrderDetailsIntent Default = new();
}

public interface ICheckoutService
{
    /// <summary>
    /// Creates the order and writes off the ingredients in ONE transaction:
    /// either both the order and the stock change exist, or neither does.
    /// Order numbers are allocated atomically per shift.
    /// <para>
    /// Payment is deferred: the order starts unpaid. The overloads below take the payment and the
    /// order-level details instead — they do not replace this one, because the shape every existing
    /// caller (and its tests) passes stays the one that works.
    /// </para>
    /// </summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, plus one payment recorded against the new order.</summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same transaction, with the order's fulfilment type, contact and requested time. A
    /// counter-service order's phone is dropped here and never reaches the database, whatever the
    /// caller passed.
    /// </summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, OrderDetailsIntent details, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, with both the payment and the order-level details.</summary>
    Task<Order> CheckoutAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent payment,
        OrderDetailsIntent details,
        CancellationToken cancellationToken = default);
}
