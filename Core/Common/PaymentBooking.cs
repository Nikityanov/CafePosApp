using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>What an order records about money, and what the cashier is told about it.</summary>
/// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

public static class PaymentBooking
{
    /// <summary>The payment to book with the order, or null to book none at all. What the sheet returned. Not null: a dismissal never reaches this.</summary>

    public static PaymentIntent? IntentFor(PaymentMethod method, decimal amount, bool isDeferred) =>
        isDeferred ? null : new PaymentIntent(amount, method);

    /// <summary>What the cart reports after the order is booked.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    public static string ReceiptText(PaymentMethod method, bool isDeferred) =>
        isDeferred
            ? "оплата не получена"
            : $"оплачено {PaymentText.Method(method)}";

    /// <summary>One shortage, in the words the operator acts on.</summary>
    /// <remarks>Почему так — `docs/decisions/checkout.md`</remarks>

    public static string Describe(StockShortage shortage) =>
        $"{shortage.IngredientName}: нужно {TextFormat.Quantity(shortage.Required, shortage.Unit)}, есть {TextFormat.Quantity(shortage.Available, shortage.Unit)}";

    /// <summary>The refusals in the order they will be listed.</summary>
    public static IReadOnlyList<string> DescribeAll(IEnumerable<StockShortage> shortages) =>
        [.. shortages.Select(Describe)];
}
