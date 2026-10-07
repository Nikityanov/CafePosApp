using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

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

            /// <summary>Rows built with their composition attached, and merged by OrderLineKey rather than by the inline product+modifier comparison this used to do. The inline comparison left the VARIANT out of the key entirely, so adding a large and a small of the same dish merged them into one line — and it could not tell two builds of one bundle apart, so two different bundles merged into one line at whichever price was added first. One key every caller computes the same way cannot drift that way again, and a mismatch is a line that visibly disagrees with the cart rather than a receipt that quietly loses one.</summary>

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

            /// <summary>Gross in and gross out, summed from the ledger rather than read off PaidKopecks, which is NET. An order refunded all the way down to zero is arithmetically identical to one that was never paid, and the summary line has to tell those two apart: only one of them ever held money, and only one of them needs a refund button.</summary>

            collectedTotal = payments.Where(payment => !payment.IsRefund).Sum(payment => payment.Amount);
            refundedTotal = payments.Where(payment => payment.IsRefund).Sum(payment => payment.Amount);

            if (AvailableProducts.Count == 0)
            {
                AvailableProducts.SyncWith((await catalog.GetProductsAsync()).Where(product => product.IsAvailable), product => product.Id);
            }

            NotifyOrderState();

            /// <summary>The formatted amounts follow the operator's currency setting, which can be changed while this page is still alive. Re-raised here rather than left to the setters above: an unchanged total raises nothing, so a page opened on «0,00 ₽» and left open across a switch to ₿ would keep printing the old sign. See MenuViewModel.RefreshMoneyText for the same argument on the cart.</summary>

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

            /// <summary>Merge through OrderLineKey, so this agrees with the cart, the add command and OrderService line for line. The inline comparison this replaced compared the product and the modifier only — the variant was not in the key at all, so a large and a small of the same dish joined one line and the quantity added up to two of something sold once.</summary>

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

    // ── Price and composition ─────────────────────────────────────────────────────────────────────

    /// <summary>Re-prices one line by hand. The allowed price is left alone, so the row shows it struck through and the shift report's discount section finds the difference afterwards.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    private async Task EditItemPriceAsync(OrderEditItemViewModel? item)
    {
        if (item is null || !CanEdit) return;

        var raw = await dialogs.PromptAsync(
            "Цена позиции",
            $"Цена за одну штуку. Сейчас {TextFormat.Money(item.Price)}.",
            Money.Round(item.Price).ToString("0.##"),
            "Сохранить",
            "Отмена");
        if (raw is null) return;

        if (!TextFormat.TryParseDecimal(raw, out var value) || value < 0)
        {
            Message = "Не удалось разобрать цену. Введите число, например 180 или 180,50.";
            haptics.Warn();
            return;
        }

        item.Price = Money.Round(value);
        NotifyTotal();
        Message = item.IsPriceOverridden
            ? $"«{item.ProductName}»: было {TextFormat.Money(item.ListPrice)}, стало {TextFormat.Money(item.Price)}. Изменение попадёт в отчёт «Скидки»."
            : $"«{item.ProductName}»: цена {TextFormat.Money(item.Price)}.";
        haptics.Click();
    }

    /// <summary>Re-opens the composition sheet for a bundle on this order — the SAME sheet the cart opens, with the same rows, the same running total and the same confirm.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    private async Task EditItemCompositionAsync(OrderEditItemViewModel? item)
    {
        if (item is null || !CanEdit || !item.IsCombo) return;

        try
        {
            var template = await combos.GetComboAsync(item.ProductId);
            if (template is null)
            {
                Message = "Комбо больше нет в каталоге. Строку придётся убрать из заказа.";
                haptics.Warn();
                return;
            }

            var options = new List<ComboSlotOption>();
            var blocked = (string?)null;

            // Ordered here rather than read off the loaded collection: ComboComponent carries no
            // SortOrder and the query applies no ORDER BY, so the collection's order is whatever the
            // join produced, and a sheet whose rows reshuffle between two opens is unusable.
            foreach (var slot in template.Components
                         .OrderBy(component => component.Product?.Name, StringComparer.CurrentCulture)
                         .ThenBy(component => component.ProductId))
            {
                var sold = BundlePlan.SellableDish(slot);
                if (sold is null)
                {
                    blocked = slot.Product?.Name ?? slot.ProductId.ToString();
                    break;
                }

                var (product, label) = sold.Value;
                /// <summary>The dish's price, not the slot's stored override — see the same change in ComboFormViewModel.UnitKopecks. The override has had no control in the form since it was cut, so feeding it here would show the operator a component price in the composition editor that the form cannot produce and the form's own total does not use. The 4th argument is the dish's real price and is now the same figure twice rather than two different ones, which is the honest thing to hand the editor.</summary>

                options.Add(new ComboSlotOption(
                    slot.ProductId,
                    label,
                    product.PriceKopecks,
                    product.PriceKopecks));
            }

            if (blocked is not null)
            {
                Message = $"Комбо «{item.ProductName}» нельзя продать: нет в наличии: {blocked}.";
                haptics.Warn();
                return;
            }

            var chosen = await comboEditor.ComposeAsync(new ComboEditorRequest(
                $"Состав комбо «{item.ProductName}»",
                options,
                item.Components.Select(component =>
                    new ComboSlotChoice(component.ProductId, component.QuantityPerUnit)).ToList(),
                template.PriceKopecks));
            if (chosen is null) return;

            // Resolved against the catalogue, not taken from the sheet: the names and both prices come
            // from ComboService, so a composition typed here cannot invent a price. A refusal names the
            // dish and is shown as written.
            var probe = new CheckoutLine(
                template.Id,
                template.Name,
                0m,
                1,
                null,
                null,
                chosen.Select(choice => new CheckoutComponent(
                    choice.ProductId, template.Name, choice.QuantityPerUnit, 0, 0)).ToList());

            IReadOnlyList<SaleComposition> resolved;
            try
            {
                resolved = await combos.ResolveSaleCompositionsAsync([probe]);
            }
            catch (ValidationFailureException exception)
            {
                Message = exception.Message;
                haptics.Warn();
                return;
            }

            var composition = resolved.FirstOrDefault();
            var components = composition?.Components ?? [];
            if (components.Count == 0)
            {
                Message = "У комбо не осталось ни одного компонента. Такую позицию лучше убрать из заказа.";
                haptics.Warn();
                return;
            }

            item.Components.Clear();
            foreach (var component in LineComponentViewModel.FromLine(components)) item.Components.Add(component);
            item.OnCompositionChanged();
            // The bundle's OWN PRICE, not the sum of its parts. Changing the composition does not
            // reprice the bundle — the price is a number in the card, and the sum is only the
            // à la carte reference the discount is measured against.
            item.Price = Money.FromKopecks(template.PriceKopecks);

            NotifyTotal();
            Message = $"Состав «{item.ProductName}» изменён, итог {TextFormat.Money(item.Price)}. Сохраните заказ.";
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to edit the composition of a line on order {OrderId}", orderId);
            Message = UserMessages.Describe(exception, "Не удалось изменить состав комбо");
            haptics.Warn();
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

        /// <summary>«Дописать» is announced here for the same reason as the two above, and it was MISSING at first: the button bound its IsVisible to this, the page inflated before LoadAsync filled `order`, and without this line the binding stayed on the inflate-time value of false — so the button was absent from a paid order and present on nothing. A Can* property nobody announces is invisible, which is a different failure from being disabled and much harder to notice in a screenshot review, because an absent button looks like a deliberate decision.</summary>

        OnPropertyChanged(nameof(CanAddContactDetails));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(CancelHint));
        OnPropertyChanged(nameof(RefundedTotal));
        OnPropertyChanged(nameof(CollectedTotal));

        /// <summary>Fulfilment, contact and promise. IsOverdue is measured against a clock rather than against anything on the entity, so it is NOT re-evaluated by a reload — a page left open on a borderline order would keep claiming a lateness that has since stopped being true. The wording and the colour follow the flag and are re-raised with it.</summary>

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

        /// <summary>The half that is easy to forget: the price and composition taps are recognizers, not Buttons, so they have no IsEnabled to bind — their liveness comes from Command.CanExecute, and nothing recomputes that unless it is told. Without this two lines the price stays tappable on a closed order and the tap opens the re-pricing sheet on a sale that is done.</summary>

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
