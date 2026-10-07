using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Editing what the order contains: adding a dish, re-pricing a line, and reopening a bundle's composition.</summary>
public partial class OrderDetailsViewModel
{

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
                /// <summary>The dish's price, not the slot's stored override — see the same change in ComboFormViewModel.UnitKopecks.</summary>
                /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

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
}
