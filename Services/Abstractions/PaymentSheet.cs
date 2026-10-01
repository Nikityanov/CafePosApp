using CafePos.Core.Models;

namespace CafePosApp.Services;

/// <summary>
/// What the payment sheet was opened for. Amounts are in rubles, matching the service boundary
/// (the entity stores kopecks; see <c>Money</c>).
/// </summary>
/// <remarks>
/// <paramref name="AmountDue"/> is what is still owed — the cart total at checkout, or the order's
/// remaining balance on a top-up. <paramref name="AlreadyPaid"/> is how much has already been paid,
/// which is non-zero only when the sheet tops up a partially paid order.
/// </remarks>
public sealed record PaymentSheetRequest(string Title, decimal AmountDue, decimal AlreadyPaid);

/// <summary>
/// The operator's declared payment: an amount in rubles and the method it was taken by.
/// </summary>
/// <remarks>
/// Card payments are a manual operator declaration — there is no acquirer or terminal behind this,
/// so the sheet only records what the operator says was taken.
/// </remarks>
public sealed record PaymentSheetResult(decimal Amount, PaymentMethod Method);

/// <summary>
/// Collects a payment from the operator. Implemented by the payment bottom sheet.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the sheet is dismissed without a decision, so a <c>null</c> result
/// means "the operator backed out" and the caller must leave the order exactly as it was.
/// </remarks>
public interface IPaymentSheet
{
    Task<PaymentSheetResult?> CollectAsync(PaymentSheetRequest request, CancellationToken cancellationToken = default);
}
