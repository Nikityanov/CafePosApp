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
                payments.Select(payment => new PaymentLine(
                    $"{TextFormat.Money(payment.Amount)} {PaymentText.Method(payment.Method)} · {payment.PaidAt.ToLocalTime():dd.MM HH:mm}")),
                line => line.Text);

            if (AvailableProducts.Count == 0)
            {
                AvailableProducts.SyncWith((await catalog.GetProductsAsync()).Where(product => product.IsAvailable), product => product.Id);
            }

            NotifyOrderState();
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

    private void NotifyTotal() => OnPropertyChanged(nameof(Total));

    private void NotifyOrderState()
    {
        OnPropertyChanged(nameof(OrderTitle));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(PaymentSummary));
        OnPropertyChanged(nameof(PaymentColor));
        OnPropertyChanged(nameof(CanCollectPayment));
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
