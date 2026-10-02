using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePosApp.Services;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Loading, editing and saving an existing order.</summary>
public partial class OrderDetailsViewModel
{
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            order = await orders.GetOrderAsync(orderId);
            if (order is null)
            {
                Message = "Заказ не найден.";
                return;
            }

            Items.SyncWith(
                order.Items.Select(item => new OrderEditItemViewModel
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    Price = item.Price,
                    SelectedModifierName = item.SelectedModifierName,
                    SelectedVariantName = item.SelectedVariantName,
                    Quantity = item.Quantity
                }),
                item => $"{item.ProductId}|{item.SelectedModifierName}|{item.SelectedVariantName}");

            var history = await orders.GetStatusHistoryAsync(orderId);
            History.SyncWith(
                history.Select(entry =>
                    $"{entry.ChangedAt.ToLocalTime():dd.MM HH:mm} · {DescribeStatus(entry.Status)}" +
                    (string.IsNullOrWhiteSpace(entry.Comment) ? string.Empty : $" ({entry.Comment})")),
                entry => entry);

            var payments = await orders.GetOrderPaymentsAsync(orderId);
            Payments.SyncWith(
                payments.Select(payment => BuildPaymentLine(payment)),
                line => line.Text);

            // Gross in and gross out, summed from the ledger rather than read off PaidKopecks, which
            // is NET. An order refunded all the way down to zero is arithmetically identical to one
            // that was never paid, and the summary line has to tell those two apart: only one of them
            // ever held money, and only one of them needs a refund button.
            collectedTotal = payments.Where(payment => !payment.IsRefund).Sum(payment => payment.Amount);
            refundedTotal = payments.Where(payment => payment.IsRefund).Sum(payment => payment.Amount);

            if (AvailableProducts.Count == 0)
            {
                AvailableProducts.SyncWith((await catalog.GetProductsAsync()).Where(product => product.IsAvailable), product => product.Id);
            }

            NotifyOrderState();

            // The formatted amounts follow the operator's currency setting, which can be changed
            // while this page is still alive. Re-raised here rather than left to the setters above:
            // an unchanged total raises nothing, so a page opened on «0,00 ₽» and left open across a
            // switch to ₿ would keep printing the old sign. See MenuViewModel.RefreshMoneyText for
            // the same argument on the cart.
            NotifyTotal();
            foreach (var item in Items) item.RefreshMoneyText();
            foreach (var line in Payments) line.RefreshMoneyText();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось загрузить заказ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddProductAsync()
    {
        var product = SelectedProduct;
        if (!CanEdit || product is null) return;

        try
        {
            var modifier = product.ModifierGroup is null ? null : await modifierPicker.PickAsync(product.ModifierGroup);
            if (product.ModifierGroup is not null && modifier is null)
            {
                Message = "Модификатор не выбран.";
                return;
            }

            var existing = Items.FirstOrDefault(item => item.ProductId == product.Id && item.SelectedModifierName == modifier);
            if (existing is null)
            {
                Items.Add(new OrderEditItemViewModel
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Price = product.Price,
                    SelectedModifierName = modifier,
                    Quantity = 1
                });
            }
            else
            {
                existing.Quantity++;
            }

            SelectedProduct = null;
            Message = string.Empty;
            NotifyTotal();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add a product to order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось добавить блюдо");
        }
    }

    private async Task SaveAsync()
    {
        if (!CanEdit)
        {
            Message = "Заказ уже нельзя изменять.";
            return;
        }

        if (Items.Count == 0)
        {
            Message = "В заказе должно остаться хотя бы одно блюдо.";
            return;
        }

        IsBusy = true;
        try
        {
            await orders.UpdateOrderAsync(orderId, Items.Select(item => item.ToOrderItem()).ToList());
            await navigation.GoBackAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось сохранить заказ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens the payment sheet for this order and books what the operator declares. A dismissed
    /// sheet leaves the order untouched.
    /// </summary>
    /// <remarks>
    /// A <see cref="ConflictException"/> with "уже оплачен" is not an error: the order may have been
    /// settled on the board since this page loaded. It is treated as a reload at warning level.
    /// </remarks>
    /// <summary>
    /// Returns money on a finished order: the amount off the keypad, the reason from a prompt.
    /// </summary>
    /// <remarks>
    /// Amount first, then reason, and that order is not arbitrary. The amount comes from the keypad
    /// so the operator sees the ceiling ("Вернуть можно: …") while deciding, and the reason is typed
    /// afterwards — the domain stores it on the refund rows and truncates it at 300 characters, so a
    /// free-text prompt before the amount would be typing into a form for a transaction whose size
    /// was not yet chosen.
    /// <para>
    /// No method is passed, and that is not an omission. The domain mirrors the payments it
    /// reverses, oldest first, because the drawer and the terminal are two real tills: booking the
    /// return under a method the money never arrived in would leave one of them wrong at the count
    /// with nothing in the app to say so.
    /// </para>
    /// <para>
    /// The reason is REQUIRED here, unlike the cancellation reason next door. It is the only record
    /// of why money left the till on a sale that genuinely happened, so it lands on the refund rows
    /// and is what an auditor reads first. An empty prompt result aborts: an unexplained refund is
    /// the one entry nobody can reconstruct afterwards.
    /// </para>
    /// </remarks>
    private async Task RefundPaymentAsync()
    {
        if (order is null || !CanRefundPayment) return;

        var result = await paymentSheet.CollectAsync(new PaymentSheetRequest(
            $"{OrderTitle} — возврат оплаты",
            Money.FromKopecks(order.PaidKopecks),
            RefundedTotal,
            PaymentSheetMode.Refund));
        if (result is null) return;

        var reason = await dialogs.PromptAsync(
            "Причина возврата",
            "Обязательно: причина возврата денег покупателю",
            string.Empty,
            "Вернуть",
            "Назад");
        if (string.IsNullOrWhiteSpace(reason))
        {
            Message = "Возврат не выполнен: не указана причина.";
            return;
        }

        try
        {
            await orders.RefundAsync(orderId, result.Amount, reason);
            await LoadAsync();
            // "Осталось" is read off the reloaded order rather than computed from the amount, so the
            // message agrees with the line above it even if the domain clamped what was applied.
            Message = $"Возвращено {TextFormat.Money(result.Amount)}. Осталось в оплате: "
                      + $"{TextFormat.Money(Money.FromKopecks(order?.PaidKopecks ?? 0))}.";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to refund order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось вернуть оплату");
            haptics.Warn();
        }
    }

    /// <summary>
    /// Voids the whole order from this page: the same confirm, stock disposition and reason as the
    /// board's cancel, in the same order.
    /// </summary>
    /// <remarks>
    /// Kept here as well as on the board because the details page is where an operator already is
    /// when they have picked one specific order to undo — and until this existed the page had no
    /// cancel control at all, so a mistake on a paid order had to be fixed by finding the right row
    /// on a different screen.
    /// <para>
    /// Reloads instead of navigating back: the order stays on screen and now reads
    /// «Отменён, возвращено …» with the refund rows in the ledger beneath it. Going back would hide
    /// the only confirmation the operator gets that the money actually moved.
    /// </para>
    /// </remarks>
    private async Task CancelOrderAsync()
    {
        if (order is null || !CanCancel) return;

        try
        {
            var paid = order.PaidKopecks;
            var paidText = TextFormat.Money(Money.FromKopecks(paid));
            var summary = paid > 0
                ? $"{OrderTitle} на {TextFormat.Money(Total)} будет отменён, покупателю вернётся {paidText}."
                : $"{OrderTitle} на {TextFormat.Money(Total)} будет отменён.";

            if (!await dialogs.ConfirmAsync("Отменить заказ?", summary, CancelText, "Назад"))
            {
                return;
            }

            var stock = await stockDisposition.ChooseAsync(new StockDispositionSheetRequest(
                OrderTitle,
                Total,
                Money.FromKopecks(paid)));
            if (stock is null) return;

            var reason = await dialogs.PromptAsync("Причина отмены", "Необязательно: причина отмены заказа", string.Empty);
            await orders.CancelOrderAsync(orderId, reason, stock.Value);

            await LoadAsync();
            Message = paid > 0
                ? $"Заказ отменён, возвращено {paidText}."
                : "Заказ отменён.";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to cancel order {OrderId} from the details page", orderId);
            Message = UserMessages.Describe(exception, "Не удалось отменить заказ");
            haptics.Warn();
        }
    }

    private async Task CollectPaymentAsync()
    {
        if (order is null || !CanCollectPayment) return;

        var payment = await paymentSheet.CollectAsync(new PaymentSheetRequest(
            OrderTitle,
            Money.FromKopecks(order.BalanceKopecks),
            Money.FromKopecks(order.PaidKopecks)));
        if (payment is null) return;

        try
        {
            await orders.AddPaymentAsync(orderId, payment.Amount, payment.Method);
            haptics.Click();
            await LoadAsync();
            Message = $"Оплата принята: {TextFormat.Money(payment.Amount)} ({PaymentText.Method(payment.Method)}).";
        }
        catch (ConflictException exception) when (exception.Message.Contains("уже оплачен", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Order {OrderId} was already paid when the payment was applied; reloading", orderId);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to accept payment for order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось принять оплату");
            haptics.Warn();
        }
    }

    private void IncreaseItem(OrderEditItemViewModel? item)
    {
        if (!CanEdit || item is null) return;
        item.Quantity++;
        NotifyTotal();
    }

    private void DecreaseItem(OrderEditItemViewModel? item)
    {
        if (!CanEdit || item is null) return;
        item.Quantity--;
        if (item.Quantity <= 0) Items.Remove(item);
        NotifyTotal();
    }

    private void RemoveItem(OrderEditItemViewModel? item)
    {
        if (!CanEdit || item is null) return;
        Items.Remove(item);
        NotifyTotal();
    }

    /// <summary>
    /// One ledger row, rendered so that money going OUT cannot be mistaken for money coming in.
    /// </summary>
    /// <remarks>
    /// A refund is the same row shape as a payment: same table, same method vocabulary, positive
    /// amount, direction carried by <c>IsRefund</c>. That is right for the invariant and wrong for a
    /// human reading a list, so before this a refund row rendered exactly like a collection row, one
    /// going in and one coming out with nothing on screen saying which was which.
    /// <para>
    /// Three signals, and all three are needed because this app never signals state by colour alone:
    /// the word "возврат" in the row text, a leading minus sign on the amount, and the danger colour
    /// the template applies from <see cref="PaymentLine.IsRefund"/>. Drop the word and a screen
    /// reader user cannot tell the rows apart; drop the sign and an operator scanning a column of
    /// figures reads the direction the wrong way; drop the colour and the two rows look like one.
    /// </para>
    /// <para>
    /// The method is still named on a refund even though the operator never chose it, because it is
    /// the method the money left by, which is exactly what a manager needs when reconciling a drawer
    /// against a terminal report. It is worded as "возврат наличными" rather than as a collection so
    /// the sentence matches the direction instead of fighting it.
    /// </para>
    /// </remarks>
    private static PaymentLine BuildPaymentLine(OrderPayment payment)
    {
        var when = payment.PaidAt.ToLocalTime().ToString("dd.MM HH:mm");
        var method = PaymentText.Method(payment.Method);
        var note = string.IsNullOrWhiteSpace(payment.Note) ? string.Empty : " · " + payment.Note;

        return new PaymentLine
        {
            Text = payment.IsRefund
                ? $"Возврат {method} · {when}{note}"
                : $"Принято {method} · {when}",
            AmountText = PaymentLine.SignedAmount(payment.Amount, payment.IsRefund),
            IsRefund = payment.IsRefund
        };
    }

    /// <summary>Re-raises the order total after an item was added, removed or re-quantitied.</summary>
    /// <remarks>
    /// <c>Total</c> is the decimal sum and <c>TotalText</c> is what the footer actually binds — the
    /// formatted amount in the operator's currency. Both are raised together: the decimal one for
    /// anything still reading the raw figure, the text one because a bound Label is never told about
    /// a dependency's change on its own.
    /// </remarks>
    private void NotifyTotal()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalText));
    }

    private void NotifyOrderState()
    {
        OnPropertyChanged(nameof(OrderTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PaymentSummary));
        OnPropertyChanged(nameof(PaymentColor));
        OnPropertyChanged(nameof(CanCollectPayment));
        OnPropertyChanged(nameof(CanRefundPayment));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(CancelHint));
        OnPropertyChanged(nameof(RefundedTotal));
        OnPropertyChanged(nameof(CollectedTotal));
        NotifyTotal();
    }

    private static string DescribeStatus(OrderStatus status) => status switch
    {
        OrderStatus.InProgress => "Создан, готовится",
        OrderStatus.Ready => "Готов к выдаче",
        OrderStatus.Completed => "Закрыт",
        OrderStatus.Cancelled => "Отменён",
        _ => status.ToString()
    };
}
