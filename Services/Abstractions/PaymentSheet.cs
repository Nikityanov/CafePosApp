using CafePos.Core.Models;

namespace CafePosApp.Services;

/// <summary>
/// What the payment sheet is doing: taking money IN, or giving it back OUT.
/// </summary>
/// <remarks>
/// The sheet is shared by both operations because the amount keypad, the window sizing, the
/// dimmed backdrop and the confirm flow are identical and were each measured once. What differs is
/// the wording and which controls exist at all.
/// <para>
/// The direction is a parameter rather than two sheet types because getting it wrong is the
/// expensive failure here: a sheet titled «Оплата заказа» for money going OUT tells the operator the
/// opposite of what they are doing, and a cash/card choice on a refund has no meaning at all — the
/// domain mirrors the original payments (see <c>IOrderService.RefundAsync</c>) because the drawer
/// and the terminal are two separate tills. So in <see cref="PaymentSheetMode.Refund"/> the method
/// row is not merely defaulted, it is not shown.
/// </para>
/// </remarks>
public enum PaymentSheetMode
{
    /// <summary>Taking a payment: shows cash/card and a «Принять оплату» confirm.</summary>
    Collect,

    /// <summary>Giving money back: hides the method row and shows «Вернуть оплату».</summary>
    Refund
}

/// <summary>
/// What the payment sheet was opened for. Amounts are in rubles, matching the service boundary
/// (the entity stores kopecks; see <c>Money</c>).
/// </summary>
/// <remarks>
/// <paramref name="AmountDue"/> is what is still owed — the cart total at checkout, or the order's
/// remaining balance on a top-up. In <see cref="PaymentSheetMode.Refund"/> it is instead the most
/// that can be returned, and the sheet refuses to offer more.
/// <paramref name="AlreadyPaid"/> is how much has already been settled in the SAME direction as the
/// current operation: money already collected on a top-up, money already given back on a refund. The
/// mode decides how the sheet words it, so the caller does not have to build a sentence.
/// <para>
/// <paramref name="Mode"/> defaults to <see cref="PaymentSheetMode.Collect"/>, so the three existing
/// call sites keep working unchanged — that default is deliberate rather than accidental; it is
/// reviewed whenever a call site is added.
/// </para>
/// </remarks>
public sealed record PaymentSheetRequest(
    string Title,
    decimal AmountDue,
    decimal AlreadyPaid,
    PaymentSheetMode Mode = PaymentSheetMode.Collect)
{
    /// <summary>True when the sheet is giving money back rather than taking it.</summary>
    public bool IsRefund => Mode == PaymentSheetMode.Refund;
}

/// <summary>
/// The operator's declared payment: an amount in rubles and the method it was taken by.
/// </summary>
/// <remarks>
/// Card payments are a manual operator declaration — there is no acquirer or terminal behind this,
/// so the sheet only records what the operator says was taken.
/// <para>
/// A refund has no method, so <see cref="Method"/> is meaningless for one and callers must ignore
/// it. It is kept non-nullable rather than split into a second result type because the caller for a
/// refund already has everything it needs from <see cref="Amount"/> and the domain refuses to take a
/// method for a refund anyway.
/// </para>
/// </remarks>
public sealed record PaymentSheetResult(decimal Amount, PaymentMethod Method);

/// <summary>
/// Collects a payment from the operator, or returns one. Implemented by the payment bottom sheet.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the sheet is dismissed without a decision, so a <c>null</c> result
/// means "the operator backed out" and the caller must leave the order exactly as it was.
/// </remarks>
public interface IPaymentSheet
{
    Task<PaymentSheetResult?> CollectAsync(PaymentSheetRequest request, CancellationToken cancellationToken = default);
}