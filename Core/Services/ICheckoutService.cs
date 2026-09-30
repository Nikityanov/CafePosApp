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

public interface ICheckoutService
{
    /// <summary>
    /// Creates the order and writes off the ingredients in ONE transaction:
    /// either both the order and the stock change exist, or neither does.
    /// Order numbers are allocated atomically per shift.
    /// </summary>
    Task<Order> CheckoutAsync(IReadOnlyList<CheckoutLine> lines, CancellationToken cancellationToken = default);
}
