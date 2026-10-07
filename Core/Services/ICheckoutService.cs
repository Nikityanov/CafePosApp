using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>One component of a bundle, as the cart holds it: what goes into it, how many, and both prices.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

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
    // The slots of a bundle, empty or `null` for an ordinary dish.
    // Почему так — `docs/decisions/checkout.md`

    IReadOnlyList<CheckoutComponent>? Components = null);

/// <summary>What the customer hands over at the counter.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

public sealed record PaymentIntent(decimal Amount, PaymentMethod Method);

/// <summary>The order-level facts the till collected: how it is fulfilled, and the contact and the requested time that go with that.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

public sealed record OrderDetailsIntent(
    OrderType OrderType = OrderType.CounterService,
    string? CustomerPhone = null,
    DateTimeOffset? RequestedAt = null)
{
    /// <summary>What every caller that says nothing gets: eaten here, no contact, as soon as possible. This is the overwhelmingly common sale and it is also the one that stores the least personal data.</summary>

    public static readonly OrderDetailsIntent Default = new();
}

public interface ICheckoutService
{
    /// <summary>Creates the order and writes off the ingredients in ONE transaction: either both the order and the stock change exist, or neither does.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, plus one payment recorded against the new order.</summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent payment, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, with the order's fulfilment type, contact and requested time. A counter-service order's phone is dropped here and never reaches the database, whatever the caller passed.</summary>

    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, OrderDetailsIntent details, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, with both the payment and the order-level details.</summary>
    Task<Order> CheckoutAsync(
        IReadOnlyList<CheckoutLine> lines,
        PaymentIntent payment,
        OrderDetailsIntent details,
        CancellationToken cancellationToken = default);
}
