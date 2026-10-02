using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>One cart line passed to the checkout.</summary>
public sealed record CheckoutLine(
    Guid ProductId,
    string ProductName,
    decimal Price,
    int Quantity,
    string? ModifierName = null,
    string? VariantName = null);

/// <summary>
/// What the customer hands over at the counter. <see cref="Amount"/> is rubles — the service/DTO
/// boundary speaks rubles and the entity speaks kopecks — and it is clamped to the order total
/// inside the checkout transaction: for a 1000 ₽ note on a 660 ₽ order the recorded payment is
/// 660 ₽ and the 340 ₽ change is the UI's business. There is no till/petty-cash model that could
/// absorb a recorded surplus.
/// </summary>
public sealed record PaymentIntent(decimal Amount, PaymentMethod Method);

public interface ICheckoutService
{
    /// <summary>
    /// Creates the order and writes off the ingredients in ONE transaction:
    /// either both the order and the stock change exist, or neither does.
    /// Order numbers are allocated atomically per shift.
    /// <para>
    /// Payment is deferred: the order starts unpaid. The overload below takes the payment instead,
    /// it does not replace this one — the two-argument shape stays the one every existing caller
    /// (and its tests) pass, and an optional <c>PaymentIntent</c> in front of the cancellation token
    /// would have made all of them ambiguous.
    /// </para>
    /// </summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);

    /// <summary>Same transaction, plus one payment recorded against the new order.</summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, PaymentIntent payment, CancellationToken cancellationToken = default);
}
