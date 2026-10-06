using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Load, reset, slot editing and save of the bundle form.</summary>
public partial class ComboFormViewModel
{
    /// <summary>
    /// Prepares the form for a bundle, or a new one when <paramref name="comboId"/> is null.
    /// </summary>
    /// <remarks>
    /// The product lists are read from the CATALOGUE rather than from the bundle's own slots, so the
    /// pickers offer every live dish and not only the ones already in use. Soft-deleted dishes are
    /// excluded on purpose: <see cref="IComboService.SaveComboAsync"/> refuses a slot pointing at one,
    /// and offering it in a picker would build a form that cannot be saved.
    /// <para>
    /// A new bundle is given <c>SortOrder = max + 1</c> so it lands at the END of the catalogue list
    /// rather than the top — the list orders by SortOrder and a brand new bundle is not the first
    /// thing an operator wants to see. One extra read, and only on the create path.
    /// </para>
    /// </remarks>
    public async Task LoadAsync(Guid? comboId)
    {
        Reset();
        editingComboId = comboId;

        var products = await catalog.GetProductsAsync(includeDeleted: false);
        ProductOptions.Clear();
        SubstituteOptions.Clear();
        SubstituteOptions.Add(new CatalogOption<Product>(null, NoSubstituteText));
        foreach (var product in products.OrderBy(product => product.Name, StringComparer.CurrentCulture))
        {
            var option = new CatalogOption<Product>(product, product.Name);
            ProductOptions.Add(option);
            SubstituteOptions.Add(option);
        }

        if (comboId is null)
        {
            var existing = await combos.GetCombosAsync(includeDeleted: true);
            sortOrder = existing.Count == 0 ? 0 : existing.Max(combo => combo.SortOrder) + 1;
            NotifyState();
            RecalculateTotal();
            return;
        }

        var combo = await combos.GetComboAsync(comboId.Value);
        if (combo is null)
        {
            ValidationMessage = "Комбо не найдено.";
            NotifyState();
            return;
        }

        ComboName = combo.Name;
        sortOrder = combo.SortOrder;
        comboPriceKopecks = combo.PriceKopecks;
        comboPriceText = Money.FromKopecks(combo.PriceKopecks).ToString("0.##");
        isComboPriceValid = combo.PriceKopecks > 0;
        OnPropertyChanged(nameof(ComboPriceText));
        OnPropertyChanged(nameof(ComboPriceKopecks));
        OnPropertyChanged(nameof(IsComboPriceValid));
        foreach (var slot in combo.Components)
        {
            var row = new ComboSlotRow(
                slot.Id == Guid.Empty ? Guid.NewGuid() : slot.Id,
                ProductOptions,
                SubstituteOptions,
                PriceModes,
                RecalculateTotal);
            row.Initialise(
                slot.ProductId,
                slot.SubstituteProductId,
                ModeOf(slot.ComponentPriceKopecks),
                slot.QuantityPerUnit,
                slot.ComponentPriceKopecks);
            Components.Add(row);
        }

        NotifyState();
        RecalculateTotal();
    }

    /// <summary>
    /// Which of the three states a stored slot price is. <c>null</c> and <c>0</c> are different
    /// decisions, so this never collapses them: a free slot would otherwise come back as a slot
    /// charging the dish's price, and the free side would quietly start costing money.
    /// </summary>
    private static ComboPriceMode ModeOf(long? componentPriceKopecks) => componentPriceKopecks switch
    {
        null => ComboPriceMode.DishPrice,
        0 => ComboPriceMode.Free,
        _ => ComboPriceMode.Fixed
    };

    public void Reset()
    {
        editingComboId = null;
        sortOrder = 0;
        ComboName = string.Empty;
        Components.Clear();
        NewSlotProductOption = null;
        NewSlotQuantityText = "1";
        ValidationMessage = string.Empty;
        comboPriceKopecks = 0;
        comboPriceText = string.Empty;
        isComboPriceValid = false;
        OnPropertyChanged(nameof(ComboPriceText));
        OnPropertyChanged(nameof(ComboPriceKopecks));
        OnPropertyChanged(nameof(IsComboPriceValid));
        OnPropertyChanged(nameof(ReferenceText));
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(DiscountHint));
        NotifyState();
        NotifySlots();
    }

    private void AddSlot()
    {
        ValidationMessage = string.Empty;

        if (NewSlotProductOption?.Value is not { } product)
        {
            ValidationMessage = "Выберите блюдо для нового компонента.";
            return;
        }

        // Refused here rather than saved and discovered at the till. The sale side resolves a
        // requested component to the FIRST slot carrying that product, so two slots for one dish are
        // not "the same dish twice" — they are one slot counted twice with a second one that can
        // never be reached. QuantityPerUnit is the honest way to say "two of this".
        if (Components.Any(slot => slot.ProductId == product.Id))
        {
            ValidationMessage = $"Блюдо «{product.Name}» уже есть в составе — увеличьте количество.";
            return;
        }

        if (!int.TryParse(NewSlotQuantityText?.Trim(), out var quantity) || quantity < 1)
        {
            ValidationMessage = "Количество должно быть целым числом не меньше 1.";
            return;
        }

        var row = new ComboSlotRow(
            Guid.NewGuid(),
            ProductOptions,
            SubstituteOptions,
            PriceModes,
            RecalculateTotal);
        row.Initialise(product.Id, null, ComboPriceMode.DishPrice, quantity, null);
        Components.Add(row);

        NewSlotProductOption = null;
        NewSlotQuantityText = "1";
        RecalculateTotal();
        haptics.Click();
    }

    /// <summary>
    /// Takes a slot out of the form. Because the save is a full replacement, this is the whole
    /// deletion — there is no separate delete call and nothing to confirm at the save, so the
    /// confirmation that matters is here.
    /// </summary>
    private async Task RemoveSlotAsync(ComboSlotRow? slot)
    {
        if (slot is null) return;
        ValidationMessage = string.Empty;

        if (Components.Count == 1)
        {
            // Said BEFORE the row goes rather than left for the save to refuse: a bundle with no
            // slots prices at zero, which is a free meal and not a bundle, and the domain turns the
            // whole save away for it — the manager would lose the whole form to a one-row edit.
            var remove = await dialogs.ConfirmAsync(
                "Убрать единственный компонент?",
                "Комбо без компонентов сохранить нельзя: его цена равна нулю. Если компонент нужен — "
                + "нажмите «Оставить».",
                "Убрать",
                "Оставить");
            if (!remove) return;
        }

        Components.Remove(slot);
        RecalculateTotal();
        haptics.Click();
    }

    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(ComboName))
        {
            ValidationMessage = "Укажите название комбо.";
            return;
        }

        // Worded as the consequence rather than as a rule, because that is what the manager needs:
        // "a combo needs at least one component" says nothing, "its price would be 0" says why not.
        if (Components.Count == 0)
        {
            ValidationMessage = "Добавьте хотя бы один компонент: без них цена комбо равна нулю.";
            return;
        }

        // The price is required and must be positive — the domain refuses a bundle at zero or less
        // (SaveComboAsync throws «Укажите цену комбо…»), and the form says so here, where the
        // mistake is, rather than at the button. Worded as the domain words it, so the two cannot
        // drift apart.
        if (!isComboPriceValid || comboPriceKopecks <= 0)
        {
            ValidationMessage = "Укажите цену комбо: товар по нулевой цене — это ошибка, а не выгодное предложение.";
            return;
        }

        if (Components.Any(slot => !slot.HasProduct))
        {
            ValidationMessage = "Укажите блюдо в каждом компоненте.";
            return;
        }

        if (Components.Any(slot => !slot.IsQuantityValid))
        {
            ValidationMessage = "Количество в каждом компоненте должно быть целым числом не меньше 1.";
            return;
        }

        // No fixed-price validation here any more, and that is a consequence of the UI cut rather
        // than a tidiness pass. The check validated what the operator had typed into the slot's
        // own amount field; that field is gone from the form, so a failure would refuse the save
        // with a message about a control that is not on screen — the operator would have no way
        // to fix it. ComponentPriceKopecks itself is still saved (see BuildSlots); it is simply
        // no longer something this form can be blamed for.

        var duplicate = Components
            .Where(slot => slot.HasProduct)
            .GroupBy(slot => slot.ProductId!.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            ValidationMessage =
                $"Блюдо «{duplicate.First().Product!.Name}» указано в составе дважды — увеличьте его количество.";
            return;
        }

        IsBusy = true;
        try
        {
            var comboId = editingComboId ?? Guid.NewGuid();
            await combos.SaveComboAsync(new Combo
            {
                Id = comboId,
                Name = ComboName.Trim(),
                PriceKopecks = comboPriceKopecks,
                SortOrder = sortOrder,
                Components = BuildSlots(comboId)
            });

            editingComboId = comboId;
            NotifyState();
            haptics.Click();
            Saved?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the combo");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить комбо");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
