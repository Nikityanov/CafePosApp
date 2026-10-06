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
    /// <summary>
    /// Taking a payment: opens on two one-tap tender buttons — «160,00 ₽ наличными» /
    /// «160,00 ₽ картой» — with a keypad and a «Принять оплату» confirm behind «Другая сумма».
    /// </summary>
    Collect,

    /// <summary>Giving money back: hides the method row and shows «Вернуть оплату».</summary>
    Refund
}

/// <summary>
/// What the payment sheet was opened for. Amounts are in rubles, matching the service boundary
/// (the entity stores kopecks; see <c>Money</c>).
/// </summary>
/// <param name="Title">The sheet's own caption: «Оплата заказа» at checkout, the order's title on a top-up.</param>
/// <param name="AmountDue">What is still owed — or, on a refund, the most that can be given back.</param>
/// <param name="AlreadyPaid">How much has already settled in this same direction.</param>
/// <param name="Mode">Which way the money moves.</param>
/// <param name="AllowDeferredPayment">
/// Whether to offer «Оплата при выдаче» as a second exit from this sheet — take the money now, or
/// take it when the order is handed over. <b>Cart checkout only.</b>
/// </param>
/// <remarks>
/// <paramref name="AmountDue"/> is what is still owed — the cart total at checkout, or the order's
/// remaining balance on a top-up. In <see cref="PaymentSheetMode.Refund"/> it is instead the most
/// that can be returned, and the sheet refuses to offer more.
/// <para>
/// It is ALSO THE QUICK-PAY FIGURE, unchanged and un-decorated: the sheet's two one-tap tender
/// buttons return <see cref="PaymentSheetResult.Amount"/> == this value, so the common sale costs
/// one tap and needs no amount typed. The amount therefore has to be non-zero for the sheet to have
/// anything to offer — at zero there are no tender buttons and no keypad, only a line saying there
/// is nothing to pay — which is why no caller gets a special case: a caller that owes nothing should
/// not be opening this sheet, and a caller that does open it gets told so rather than handed a
/// button that books nothing.
/// </para>
/// <paramref name="AlreadyPaid"/> is how much has already been settled in the SAME direction as the
/// current operation: money already collected on a top-up, money already given back on a refund. The
/// mode decides how the sheet words it, so the caller does not have to build a sentence.
/// <para>
/// <paramref name="Mode"/> defaults to <see cref="PaymentSheetMode.Collect"/>, so the four existing
/// call sites keep working unchanged — that default is deliberate rather than accidental; it is
/// reviewed whenever a call site is added.
/// </para>
/// <para>
/// <paramref name="AllowDeferredPayment"/> defaults to false, and that default is the reason the
/// deferred exit cannot reach a screen it does not belong on. This sheet is ALSO the payment step for
/// an order that already exists — the top-up on the board and «Принять оплату» on the details page
/// both open it — and on those paths there is nothing to create and nothing deferred to decide: the
/// order is on the board and the balance is simply outstanding. A «Создать без оплаты» button there
/// would be a create-order action on the collect-payment path, so only
/// <c>MenuViewModel.PayAndCreateAsync</c>, the one path whose subject does not exist yet, passes true.
/// One-tap payment did not change this, and the deferred exit stays visible on the sheet's opening
/// panel rather than being pushed behind the keypad.
/// </para>
/// </remarks>
public sealed record PaymentSheetRequest(
    string Title,
    decimal AmountDue,
    decimal AlreadyPaid,
    PaymentSheetMode Mode = PaymentSheetMode.Collect,
    bool AllowDeferredPayment = false)
{
    /// <summary>True when the sheet is giving money back rather than taking it.</summary>
    public bool IsRefund => Mode == PaymentSheetMode.Refund;

    /// <summary>
    /// True when the sheet should show the «Оплата при выдаче» exit. Never on a refund, whatever the
    /// caller asked for.
    /// </summary>
    public bool ShowsDeferredPayment => AllowDeferredPayment && !IsRefund;
}

/// <summary>
/// The operator's declared payment: an amount in rubles and the method it was taken by.
/// </summary>
/// <remarks>
/// Card payments are a manual operator declaration — there is no acquirer or terminal behind this,
/// so the sheet only records what the operator says was taken.
/// <para>
/// This record is deliberately wide enough for BOTH the one-tap and the keyed path, and that is the
/// load-bearing decision in the whole feature: a tender button and a keypad confirm return the SAME
/// type with the same meaning, differing only in the figure. One tap on «160,00 ₽ наличными»
/// returns <c>Amount = 160, Method = Cash</c>; typing 60 and confirming returns
/// <c>Amount = 60, Method = Cash</c>. Neither needs a flag saying which produced it, because no
/// caller has any business knowing — the operator's payment is the operator's payment.
/// </para>
/// <para>
/// <b>NOTHING WAS ADDED HERE FOR ONE-TAP PAYMENT.</b> Both fields already existed and already
/// carried the amounts; adding a <c>IsQuickPay</c> would have been the tempting way to make the
/// sheet's internal state visible, and it would have pushed a branch into all four call sites for
/// no decision any of them makes.
/// </para>
/// <para>
/// A refund has no method, so <see cref="Method"/> is meaningless for one and callers must ignore
/// it. It is kept non-nullable rather than split into a second result type because the caller for a
/// refund already has everything it needs from <see cref="Amount"/> and the domain refuses to take a
/// method for a refund anyway.
/// </para>
/// <para>
/// <paramref name="IsDeferred"/> is the third way out of the sheet, and for the same reason as the
/// method above it carries no money: the operator chose «Оплата при выдаче», so nothing was collected.
/// <see cref="Amount"/> is then meaningless and callers must ignore it. A separate result TYPE was not
/// introduced for it because the caller branches on the flag exactly as it already branches on
/// <c>request.IsRefund</c>, and <c>PaymentMethod.Cash</c> is written as a filler for the same reason
/// the field cannot be nullable: there is nothing to declare, and the domain will take no method for
/// an unpaid order.
/// </para>
/// </remarks>
/// <param name="Amount">What was charged: the whole balance from a tender button, or what the operator keyed in. In whole rubles.</param>
/// <param name="Method">How they said it was taken.</param>
/// <param name="IsDeferred">
/// True when the operator chose to pay on collection instead. Only ever set on a request that asked
/// for it — see <see cref="PaymentSheetRequest.AllowDeferredPayment"/>.
/// </param>
public sealed record PaymentSheetResult(decimal Amount, PaymentMethod Method, bool IsDeferred = false);

/// <summary>
/// Collects a payment from the operator, or returns one. Implemented by the payment bottom sheet.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the sheet is dismissed without a decision, so a <c>null</c> result
/// means "the operator backed out" and the caller must leave the order exactly as it was.
/// <para>
/// A non-null result is a DECISION, not a question. On the collect path the operator has either
/// charged something (one tap on a tender button, or a keyed amount confirmed) or deferred the
/// payment, and the caller handles all three identically — book <see cref="PaymentSheetResult.Amount"/>
/// by <see cref="PaymentSheetResult.Method"/>, unless <see cref="PaymentSheetResult.IsDeferred"/>.
/// Which of the three the sheet should have offered is entirely the sheet's business.
/// </para>
/// </remarks>
public interface IPaymentSheet
{
    Task<PaymentSheetResult?> CollectAsync(PaymentSheetRequest request, CancellationToken cancellationToken = default);
}