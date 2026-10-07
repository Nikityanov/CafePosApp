using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>What happens to the money and to the order itself: refunds, collecting payment, and voiding the sale.</summary>
public partial class OrderDetailsViewModel
{

    /// <summary>Opens the payment sheet for this order and books what the operator declares. A dismissed sheet leaves the order untouched. Returns money on a finished order: the amount off the keypad, the reason from a prompt.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

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

    /// <summary>Voids the whole order from this page: the same confirm, stock disposition and reason as the board's cancel, in the same order.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

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

    /// <summary>One ledger row, rendered so that money going OUT cannot be mistaken for money coming in.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

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
}
