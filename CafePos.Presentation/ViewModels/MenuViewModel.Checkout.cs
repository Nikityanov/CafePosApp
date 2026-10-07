using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Checkout, cart autosave and parked ("held") carts.</summary>
public partial class MenuViewModel
{
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
    private OrderDetailsIntent BuildOrderDetails() => new(OrderType, CustomerPhone, RequestedAt);

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
    private async Task PayAndCreateAsync()
    {
        if (Cart.Count == 0)
        {
            Message = "Добавьте товары в заказ.";
            return;
        }

        IsBusy = true;
        try
        {
            var lines = await PrepareCheckoutAsync();

            var payment = await paymentSheet.CollectAsync(
                new PaymentSheetRequest("Оплата заказа", Total, 0, AllowDeferredPayment: true));
            if (payment is null) return;

            // One decision, taken once: what the order records about money AND what the operator is told.
            // Two independent ternaries over the same flag is how an unpaid order ends up announced as
            // paid. See PaymentBooking.
            var intent = PaymentBooking.IntentFor(payment.Method, payment.Amount, payment.IsDeferred);

            var order = intent is null
                ? await checkout.CheckoutAsync(lines, BuildOrderDetails())
                : await checkout.CheckoutAsync(lines, intent, BuildOrderDetails());

            await FinishOrderCreatedAsync(order, PaymentBooking.ReceiptText(payment.Method, payment.IsDeferred));
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout failed");
            await HandleCheckoutFailureAsync(exception);
            haptics.Warn();
        }
        finally
        {
            IsBusy = false;
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
    private async Task HandleCheckoutFailureAsync(Exception exception)
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
                await navigation.GoToOpenShiftAsync();
                return;
            }

            SetError(exception, "Смена не открыта");
            return;
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
        SetError(exception, "Не удалось оформить заказ");
    }

    /// <summary>
    /// Clears the cart and reports what happened. The message names the outcome — paid by which
    /// method, or unpaid — so the operator does not have to open the board to find out.
    /// </summary>
    /// <remarks>
    /// The order-level facts are cleared with the cart, and that is a privacy requirement rather than
    /// tidiness: a phone left on an emptied screen would be carried into the NEXT order and written to
    /// it by <see cref="BuildOrderDetails"/> with nobody having entered it for that sale. The fulfilment
    /// type goes back to counter service for the same reason — the default is the sale that stores the
    /// least personal data, so a phone can never reach an order by inertia.
    /// </remarks>
    private async Task FinishOrderCreatedAsync(Order order, string paymentText)
    {
        Cart.Clear();
        Recalculate();

        // One call, not three assignments. These were cleared field by field, which meant a fourth
        // fact added to the order later would silently survive the reset unless someone remembered
        // this line. FulfilmentEditor.Reset is where the list of what a new order starts without lives.
        fulfilment.Reset();
        // The cart is no longer the restored draft; it is a new, unstarted one. Left in place it
        // would head an empty «Корзина» with a note about an order that has now been paid for.
        DraftNotice = string.Empty;
        await drafts.ClearActiveCartAsync();
        Message = $"Заказ #{order.OrderNumber} создан на {TextFormat.Money(order.TotalPrice)}, {paymentText}.";
    }

    private async Task ParkOrderAsync()
    {
        if (Cart.Count == 0)
        {
            Message = "Чек пуст.";
            return;
        }

        try
        {
            var suggestedName = $"Чек {timeProvider.GetLocalNow():HH:mm}";
            var name = await dialogs.PromptAsync("Отложить чек", "Название отложенного чека", suggestedName);
            if (name is null) return;

            var parked = await drafts.ParkAsync(name, cart.ToCheckoutLines());
            Cart.Clear();
            Recalculate();
            // The lines now belong to a parked receipt with its own name, so the header has no
            // unsaved-draft fact left to state.
            DraftNotice = string.Empty;

            // The order-level facts are dropped with the cart, and this is a KNOWN GAP rather than a
            // decision: DraftOrder has OrderType / CustomerPhone / RequestedAt columns and
            // DraftOrderItem has its Components table, but IDraftOrderService takes only a line list and
            // hands back a CartSnapshot of lines — there is no parameter to write those three columns
            // through and none in the shape returned to read them back. So parking a cart that has a
            // phone and a chosen time loses both, and this reset is what keeps them from being carried
            // into the next order instead. The Core side needed is: an OrderDetailsIntent parameter on
            // ParkAsync/SaveActiveCartAsync and on CartSnapshot, plus a ThenInclude on Components in
            // LoadActiveCartAsync/TakeAsync.
            fulfilment.Reset();

            await drafts.ClearActiveCartAsync();
            Message = $"Чек отложен: {parked.Name}.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to park the cart");
            SetError(exception, "Не удалось отложить чек");
        }
    }

    private async Task OpenParkedAsync()
    {
        try
        {
            var parked = await drafts.GetParkedAsync();
            if (parked.Count == 0)
            {
                Message = "Отложенных чеков нет.";
                return;
            }

            if (Cart.Count > 0 && !await dialogs.ConfirmAsync("Отложенные чеки", "Текущий чек будет очищен и заменён отложенным. Продолжить?")) return;

            var draftId = await draftPicker.PickAsync(parked);
            if (draftId is null) return;

            var snapshot = await drafts.TakeAsync(draftId.Value);
            Cart.Clear();
            // WithComponents, so a parked bundle comes back with its slots — see RestoreDraftAsync.
            cart.RestoreLines([.. snapshot.Lines.Select(line => CartItemViewModel.FromLine(line).WithComponents(line.Components))]);
            Recalculate();
            // A parked receipt is not an unsaved one: the header note is specifically about lines
            // recovered from a draft after the app went away, and would be a false statement here.
            DraftNotice = string.Empty;
            Message = "Отложенный чек загружен.";
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to open a parked cart");
            SetError(exception, "Не удалось открыть отложенный чек");
        }
    }

    /// <summary>
    /// Debounced autosave. The lines are snapshotted on the calling (UI) thread, the write itself
    /// happens after a short delay so typing/stepping does not hit the database on every tap.
    /// </summary>
    private void ScheduleAutoSave()
    {
        // Cancel but do NOT dispose. The superseded AutoSaveAsync still holds this token and
        // may be inside drafts.SaveActiveCartAsync right now; disposing a CancellationTokenSource
        // whose token is in use is a race of its own. The superseded run disposes its own source
        // when it finishes.
        autoSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        autoSaveCancellation = cancellation;

        var lines = cart.ToCheckoutLines();
        _ = AutoSaveAsync(lines, cancellation);
    }

    private async Task AutoSaveAsync(IReadOnlyList<CheckoutLine> lines, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, cancellation.Token);

            // The gate is taken AFTER the debounce and released in finally, so an overlapping
            // snapshot waits for the in-flight write instead of racing it. The superseded
            // snapshot is already cancelled by then and its SaveChanges is skipped, so the
            // waiter writes the newer lines and the last write still wins.
            await autoSaveGate.WaitAsync(cancellation.Token);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await drafts.SaveActiveCartAsync(lines, cancellation.Token);
            }
            finally
            {
                autoSaveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change superseded this snapshot.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cart autosave failed");
        }
        finally
        {
            // Only the source this run owns, and only if it has not already been replaced.
            if (ReferenceEquals(autoSaveCancellation, cancellation)) autoSaveCancellation = null;
            cancellation.Dispose();
        }
    }
}
