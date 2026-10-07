using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Loading the order, saving edits, adding contact details late, and re-raising what the bindings read.</summary>
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

            /// <summary>Rows built with their composition attached, and merged by OrderLineKey rather than by the inline product+modifier comparison this used to do.</summary>
            /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

            var rows = order.Items
                .Select(item =>
                {
                    var row = new OrderEditItemViewModel
                    {
                        ProductId = item.ProductId,
                        ProductName = item.ProductName,
                        Price = item.Price,
                        ListPrice = Money.FromKopecks(item.ListPriceKopecks),
                        SelectedModifierName = item.SelectedModifierName,
                        SelectedVariantName = item.SelectedVariantName,
                        Quantity = item.Quantity
                    };
                    foreach (var component in LineComponentViewModel.FromItem(item.Components)) row.Components.Add(component);
                    return row;
                })
                .ToList();

            Items.SyncWith(rows, item => item.MergeKey);

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

            /// <summary>Gross in and gross out, summed from the ledger rather than read off PaidKopecks, which is NET.</summary>
            /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

            collectedTotal = payments.Where(payment => !payment.IsRefund).Sum(payment => payment.Amount);
            refundedTotal = payments.Where(payment => payment.IsRefund).Sum(payment => payment.Amount);

            if (AvailableProducts.Count == 0)
            {
                AvailableProducts.SyncWith((await catalog.GetProductsAsync()).Where(product => product.IsAvailable), product => product.Id);
            }

            NotifyOrderState();

            /// <summary>The formatted amounts follow the operator's currency setting, which can be changed while this page is still alive.</summary>
            /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

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

            /// <summary>Merge through OrderLineKey, so this agrees with the cart, the add command and OrderService line for line.</summary>
            /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

            var existing = Items.FirstOrDefault(item => item.MergeKey == OrderLineKey.For(product.Id, modifier, null));

            if (existing is null)
            {
                Items.Add(new OrderEditItemViewModel
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Price = product.Price,
                    // Nothing overridden yet, so the allowed price and the charged price are one number.
                    ListPrice = product.Price,
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

    /// <summary>Adds a phone and a promised time to an order that is already paid for, because the customer thought of them after the money changed hands.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    private async Task AddContactDetailsAsync()
    {
        if (!CanAddContactDetails || order is null) return;

        var sheetResult = await contactDetailsSheet.ShowAsync(new ContactDetailsSheetRequest(
            $"{OrderTitle} — дописать",
            order.CustomerPhone,
            order.RequestedAt,
            order.OrderType == OrderType.Takeaway));

        if (sheetResult is null) return;

        try
        {
            await orders.AddContactDetailsAsync(
                orderId,
                sheetResult.Phone,
                sheetResult.PromisedAt,
                sheetResult.PromoteToTakeaway);

            // Reloaded rather than patched: the same call may have changed the fulfilment type, and the
            // order card states the type in words. A message built off the pre-write order would report a
            // state the screen no longer shows.
            await LoadAsync();

            // Says WHAT was recorded, from the reloaded order. An order written with a phone but no
            // promised time must not be reported as having both.
            var recordedPhone = order?.CustomerPhone is not null;
            var recordedTime = order?.RequestedAt is not null;

            Message = (recordedPhone, recordedTime) switch
            {
                (true, true) => "Записаны телефон и время.",
                (true, false) => "Записан телефон.",
                (false, true) => "Записано время выдачи.",
                _ => "Ничего не записано."
            };

            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add contact details to order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось дописать в заказ");
            haptics.Warn();
        }
    }

    /// <remarks>`docs/decisions/order-details.md`</remarks>

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

        /// <summary>«Дописать» is announced here for the same reason as the two above, and it was MISSING at first: the button bound its IsVisible to this, the page inflated before...</summary>
        /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

        OnPropertyChanged(nameof(CanAddContactDetails));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(CancelHint));
        OnPropertyChanged(nameof(RefundedTotal));
        OnPropertyChanged(nameof(CollectedTotal));

        /// <summary>Fulfilment, contact and promise.</summary>
        /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

        OnPropertyChanged(nameof(HasOrderDetails));
        OnPropertyChanged(nameof(OrderTypeText));
        OnPropertyChanged(nameof(CustomerPhoneText));
        OnPropertyChanged(nameof(HasCustomerPhone));
        OnPropertyChanged(nameof(ExpectsPhone));
        OnPropertyChanged(nameof(ExpectsNoPhone));
        OnPropertyChanged(nameof(PromiseText));
        // The collapsed row reads OrderTypeText and PromisedAt, neither of which the entity raises
        // for us — it is re-read on every load here.
        OnPropertyChanged(nameof(FulfilmentSummary));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(OverdueMinutes));
        OnPropertyChanged(nameof(OverdueText));
        OnPropertyChanged(nameof(OverdueColor));

        /// <summary>The half that is easy to forget: the price and composition taps are recognizers, not Buttons, so they have no IsEnabled to bind — their liveness comes from Comm...</summary>
        /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

        EditItemPriceCommand.NotifyCanExecuteChanged();
        EditItemCompositionCommand.NotifyCanExecuteChanged();

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
