using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>The sale: pre-flight the stock, take the money, book the order.</summary>
/// <remarks>
/// <para>
/// The sixth and last Collaborator. NINE dependencies against the plan's three, and each of the
/// extra six is here because this responsibility really needs it - see the constructor for the list.
/// </para>
/// <para>
/// <b>WHAT IT DOES NOT DO.</b> It does not clear the cart, the fulfilment facts or the draft notice.
/// It RETURNS a <see cref="SaleResult"/> and the shell applies the clearing. That is what keeps
/// <c>FulfilmentEditor</c> and <c>DraftAutosave</c> out of its constructor, and it is also the honest
/// direction of travel: "an order was booked" is a fact about the sale; "the till is now empty" is a
/// fact about the screen.
/// </para>
/// <para>
/// <b>WHY THERE IS NO <c>INavigationService</c>.</b> The no-shift branch asks the operator whether to
/// open the shift and then used to open the screen itself. Navigation is not checkout's business, so
/// the question is asked here and the answer is returned as <see cref="SaleOutcome.OfferShift"/>.
/// Reading it off the domain's message text instead would break the moment anyone reworded it.
/// </para>
/// <para>
/// Parking did NOT come here. <c>ParkOrderAsync</c> and <c>OpenParkedAsync</c> were in the same file,
/// but parking a receipt is not selling anything - no money, no stock check, no order - and the class
/// they belong to is <c>ParkedCarts</c>. This file being one file was the reason the plan named one
/// class for both, which is the same mistake as moving a section rather than a responsibility.
/// </para>
/// </remarks>
public sealed class CheckoutCoordinator
{
    private readonly ICheckoutService checkout;
    private readonly IPaymentSheet paymentSheet;
    private readonly IInventoryService inventory;
    private readonly IShiftSession shiftSession;
    private readonly IDialogService dialogs;
    private readonly CartBuilder cart;
    private readonly IHapticService haptics;
    private readonly ILogger logger;
    private readonly Action<string> announce;
    private readonly Action<string> sayError;
    private readonly Action<bool> setBusy;

    /// <param name="announce">Sets a neutral message. The assignment it stands for, verbatim.</param>
    /// <param name="sayError">Sets a message already worded and flags it as a failure.</param>
    /// <param name="setBusy">Sets or clears the shell's <c>IsBusy</c>, which also disables the button.</param>
    public CheckoutCoordinator(
        ICheckoutService checkout,
        IPaymentSheet paymentSheet,
        IInventoryService inventory,
        IShiftSession shiftSession,
        IDialogService dialogs,
        CartBuilder cart,
        IHapticService haptics,
        ILogger logger,
        Action<string> announce,
        Action<string> sayError,
        Action<bool> setBusy)
    {
        this.checkout = checkout;
        this.paymentSheet = paymentSheet;
        this.inventory = inventory;
        this.shiftSession = shiftSession;
        this.dialogs = dialogs;
        this.cart = cart;
        this.haptics = haptics;
        this.logger = logger;
        this.announce = announce;
        this.sayError = sayError;
        this.setBusy = setBusy;
    }

    /// <summary>
    /// The order-level facts this cart carries, gathered above the cart and passed as one value.
    /// </summary>
    /// <remarks>
    /// One parameter rather than three, because they are facts about the ORDER and not about any line:
    /// a customer either takes the whole thing away or eats all of it on the premises. Carrying them
    /// per line would allow one order to be half takeaway.
    /// <para>
    /// The phone goes in as typed and is normalised by the service, which also DROPS it for counter
    /// service — so passing one here for a counter order would be pointless as well as unlawful. The
    /// control makes that unreachable, and the service makes it enforced.
    /// </para>
    /// </remarks>
    private static OrderDetailsIntent BuildOrderDetails(OrderDetailsIntent facts) => facts;

    /// <summary>
    /// Snapshots the cart and pre-flights the stock check shared by both checkout paths, so the
    /// operator gets an actionable shortage message before any payment is taken.
    /// </summary>
    /// <remarks>
    /// The pre-flight runs over <see cref="ComboExpander.Expand"/> rather than over the lines
    /// themselves. A bundle is a rollup with no recipe of its own, so asking the planner about the
    /// bundle finds no ingredients and reports no shortage — the exact "checked and fine" that lets an
    /// order through and then fails at the till. Expanded, the pre-flight sees the same demands the
    /// real write-off will produce, which is the whole point of expanding rather than teaching the
    /// planner about bundles.
    /// </remarks>
    private async Task<IReadOnlyList<CheckoutLine>> PrepareCheckoutAsync()
    {
        var lines = cart.ToCheckoutLines();

        var shortages = await inventory.PreviewShortagesAsync(
            ComboExpander.Expand(lines)
                .Select(demand => (demand.ProductId, demand.Quantity))
                .ToList());

        if (shortages.Count > 0)
        {
            throw new InsufficientStockException(PaymentBooking.DescribeAll(shortages));
        }

        return lines;
    }

    /// <summary>
    /// «Оплатить {total}»: open the payment sheet, then book the order — paid, or to be paid on
    /// collection, whichever the operator chose in the sheet. The sheet is opened before the checkout
    /// so a dismissed sheet leaves the cart untouched.
    /// </summary>
    /// <remarks>
    /// <b>«Оплата при выдаче» USED TO BE A SECOND BUTTON HERE AND IS NOW A SECOND EXIT FROM THE
    /// SHEET.</b> Customers very often pay when they collect rather than when they order, so the
    /// choice is real and has to stay — but it is a choice about WHEN money moves, so it now lives at
    /// the moment money moves. Two buttons became one, and this method absorbed the branch: it is the
    /// only place that books an order, so the decision cannot be taken anywhere else.
    /// <para>
    /// The sheet is the right place for it rather than merely a tidier one. On the cart, the two
    /// buttons were a fork the operator had to remember; in the sheet, «Принять оплату» and «Оплата при
    /// выдаче» answer the same question in the same place, and the one that does not take the money
    /// cannot be mistaken for the one that does. Baymard's 2024 study caught a tester asking, of a
    /// «Next» button, verbatim: «I'm not sure if I click the 'Next' button, will it charge?» — the
    /// sheet's own buttons answer that by naming what each does.
    /// </para>
    /// <para>
    /// <b>ONLY THIS PATH OFFERS IT</b>, and the reason is that the same sheet is also the payment step
    /// for an order that already exists (the board's top-up, «Принять оплату» on the details page).
    /// There is nothing to create on that path and nothing to defer. See
    /// <see cref="PaymentSheetRequest.AllowDeferredPayment"/>.
    /// </para>
    /// </remarks>
    public async Task<SaleResult> PayAndCreateAsync(OrderDetailsIntent facts)
    {
        if (cart.Cart.Count == 0)
        {
            announce("Добавьте товары в заказ.");
            return SaleResult.Refused;
        }

        setBusy(true);
        try
        {
            var lines = await PrepareCheckoutAsync();

            var payment = await paymentSheet.CollectAsync(
                new PaymentSheetRequest("Оплата заказа", cart.Total, 0, AllowDeferredPayment: true));

            // A dismissed sheet is a dismissal, not a refusal. Nothing was attempted, and the cart is
            // exactly as it was - which is the whole reason the sheet is opened before the booking.
            if (payment is null) return SaleResult.Dismissed;

            // One decision, taken once: what the order records about money AND what the operator is told.
            // Two independent ternaries over the same flag is how an unpaid order ends up announced as
            // paid. See PaymentBooking.
            var intent = PaymentBooking.IntentFor(payment.Method, payment.Amount, payment.IsDeferred);

            var order = intent is null
                ? await checkout.CheckoutAsync(lines, facts)
                : await checkout.CheckoutAsync(lines, intent, facts);

            haptics.Click();
            return new SaleResult(
                SaleOutcome.Booked,
                order.OrderNumber,
                order.TotalPrice,
                PaymentBooking.ReceiptText(payment.Method, payment.IsDeferred));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout failed");
            var result = await HandleCheckoutFailureAsync(exception);
            haptics.Warn();
            return result;
        }
        finally
        {
            setBusy(false);
        }
    }

    /// <summary>
    /// Reports a failed checkout, and offers the opening screen when the reason is that there is no
    /// shift.
    /// </summary>
    /// <remarks>
    /// Checked against the session rather than against the domain's message text. The refusal is a
    /// <c>ConflictException</c> whose wording is written for a human, and matching on that string
    /// would break the moment anybody rewords it — silently, by sending the operator to a screen
    /// that has nothing to do with what went wrong.
    /// <para>
    /// The CART IS KEPT. The operator built it before pressing the button, and losing it because the
    /// shift happened to be closed would be the most expensive possible way to ask them to open one.
    /// </para>
    /// </remarks>
    private async Task<SaleResult> HandleCheckoutFailureAsync(Exception exception)
    {
        await shiftSession.RefreshAsync();
        if (!shiftSession.IsShiftOpen)
        {
            if (await dialogs.ConfirmAsync(
                    "Смена не открыта",
                    "Продавать нельзя, пока смена не открыта и в кассу не внесён размен. Открыть смену сейчас?",
                    "Открыть смену",
                    "Отмена"))
            {
                // The question is asked HERE and the answer goes back as a result. Opening the screen
                // is navigation, and this class has no business holding a navigation seam - see the
                // note on SaleOutcome.OfferShift.
                return SaleResult.OfferShift;
            }

            sayError(UserMessages.Describe(exception, "Смена не открыта"));
            return SaleResult.Refused;
        }

        // The shift IS open, so there is nothing left to offer: no screen to send the operator to and
        // no state to refresh. This line used to be `await HandleCheckoutFailureAsync(exception)` —
        // the method calling itself with no overload in between. Every checkout failure with a shift
        // open (out of stock, a price that moved, anything at all) therefore re-entered this method,
        // found the shift still open, and recursed until the stack ran out: StackOverflowException,
        // which is not catchable and takes the process down with no message and no dialog. The common
        // case was the crash, and the rare case (no shift) was the only one that ever worked.
        //
        // What belongs here is the same thing the no-shift branch does one line up: report it. The
        // reason this is worth a paragraph is that the guard above looks like it covers both cases,
        // and it does — the bug lived entirely in what came after it.
        sayError(UserMessages.Describe(exception, "Не удалось оформить заказ"));
        return SaleResult.Refused;
    }
}
