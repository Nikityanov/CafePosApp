using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Checkout, cart autosave and parked ("held") carts.</summary>
public partial class MenuViewModel
{
    /// <summary>
    /// Snapshots the cart and pre-flights the stock check shared by both checkout paths, so the
    /// operator gets an actionable shortage message before any payment is taken.
    /// </summary>
    private async Task<IReadOnlyList<CheckoutLine>> PrepareCheckoutAsync()
    {
        var lines = Cart.Select(item => item.ToCheckoutLine()).ToList();

        // Pre-flight check so the operator gets an actionable message before the transaction.
        var shortages = await inventory.PreviewShortagesAsync(lines.Select(line => (line.ProductId, line.Quantity)).ToList());
        if (shortages.Count > 0)
        {
            throw new InsufficientStockException(shortages
                .Select(shortage => $"{shortage.IngredientName}: нужно {shortage.Required:0.##} {shortage.Unit}, есть {shortage.Available:0.##} {shortage.Unit}")
                .ToList());
        }

        return lines;
    }

    /// <summary>
    /// «Оплатить и создать»: take the payment first, then book the order paid. The sheet is opened
    /// before the checkout so a dismissed sheet leaves the cart untouched.
    /// </summary>
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

            var payment = await paymentSheet.CollectAsync(new PaymentSheetRequest("Оплата заказа", Total, 0));
            if (payment is null) return;

            var order = await checkout.CheckoutAsync(lines, new PaymentIntent(payment.Amount, payment.Method));

            await FinishOrderCreatedAsync(order, $"оплачено {PaymentText.Method(payment.Method)}");
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout with payment failed");
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
    /// «Создать без оплаты»: book the order unpaid. It shows as unpaid on the board and payment
    /// can be collected later from its card or the details page.
    /// </summary>
    private async Task CreateWithoutPaymentAsync()
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
            var order = await checkout.CheckoutAsync(lines);
            await FinishOrderCreatedAsync(order, "оплата не получена");
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
    /// Clears the cart and reports what happened. The message names the outcome — paid by which
    /// method, or unpaid — so the operator does not have to open the board to find out.
    /// </summary>
    private async Task FinishOrderCreatedAsync(Order order, string paymentText)
    {
        Cart.Clear();
        Recalculate();
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

            var parked = await drafts.ParkAsync(name, Cart.Select(item => item.ToCheckoutLine()).ToList());
            Cart.Clear();
            Recalculate();
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
            foreach (var line in snapshot.Lines) Cart.Add(CartItemViewModel.FromLine(line));
            Recalculate();
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

        var lines = Cart.Select(item => item.ToCheckoutLine()).ToList();
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
