using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePos.Core.Common;

/// <summary>
/// What an order records about money, and what the cashier is told about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>THESE TWO ANSWERS MUST NOT DISAGREE, AND NOTHING ENFORCED THAT.</b> The checkout path had two
/// independent ternaries over the same flag: whether the order is written with a payment row, and
/// whether the operator is then told «оплачено картой» or «оплата не получена». Change one and not the
/// other and you get an order that says it was paid with no payment behind it — or the reverse, an
/// unpaid order announced as paid. Both are silent, and one is a till that stops balancing.
/// </para>
/// <para>
/// The rule they encode: a deferred payment books the order with NO <see cref="PaymentIntent"/> at
/// all, the same overload without one, so the two paths write an identical order and differ only in
/// whether a payment row exists. Nothing is clamped, defaulted or invented — deferred means the
/// operator declined to take money, and the only honest record of that is its absence.
/// </para>
/// </remarks>
public static class PaymentBooking
{
    /// <summary>
    /// The payment to book with the order, or <c>null</c> to book none at all.
    /// </summary>
    /// <param name="payment">What the sheet returned. Not null: a dismissal never reaches this.</param>
    public static PaymentIntent? IntentFor(PaymentMethod method, decimal amount, bool isDeferred) =>
        isDeferred ? null : new PaymentIntent(amount, method);

    /// <summary>
    /// What the cart reports after the order is booked.
    /// </summary>
    /// <remarks>
    /// Derived from the SAME flag as <see cref="IntentFor"/>, and deliberately phrased so neither
    /// reading can pass for the other: «оплата не получена» says the money has not arrived, which is
    /// not the same claim as «оплата отложена» — one is a fact about now, the other a promise about
    /// later, and the first is the one the operator needs.
    /// </remarks>
    public static string ReceiptText(PaymentMethod method, bool isDeferred) =>
        isDeferred
            ? "оплата не получена"
            : $"оплачено {PaymentText.Method(method)}";

    /// <summary>
    /// One shortage, in the words the operator acts on.
    /// </summary>
    /// <remarks>
    /// <b>BOTH FIGURES, NOT JUST THE VERDICT.</b> «Недостаточно молока» tells the cashier the sale is
    /// refused and nothing about what to do; «нужно 0,5 л, есть 0,2 л» tells them whether to make the
    /// latte, substitute, or offer something else.
    /// <para>
    /// The quantities go through <see cref="TextFormat.Quantity"/> rather than a format string written
    /// here. That is the app's existing convention for a stock amount — trailing zeros dropped, three
    /// decimals kept so 0,25 is not rounded away — and a second formatting rule for the same kind of
    /// figure is a second thing to keep in step. The decimal separator follows the device's culture,
    /// as it does everywhere else in the app.
    /// </para>
    /// </remarks>
    public static string Describe(StockShortage shortage) =>
        $"{shortage.IngredientName}: нужно {TextFormat.Quantity(shortage.Required, shortage.Unit)}, есть {TextFormat.Quantity(shortage.Available, shortage.Unit)}";

    /// <summary>The refusals in the order they will be listed.</summary>
    public static IReadOnlyList<string> DescribeAll(IEnumerable<StockShortage> shortages) =>
        [.. shortages.Select(Describe)];
}