using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Behaviour of the menu / cart screen.</summary>
public partial class MenuViewModel
{
    private void SelectCategory(CategoryMenuItemViewModel? item)
    {
        menu.SelectCategory(item);
        menu.HighlightSelectedChip();
        menu.ApplyFilters();
    }
    public async Task LoadAsync()
    {
        await loadGate.WaitAsync();
        IsBusy = true;
        try
        {
            // Whatever cart is restored below is not the cart an undo was armed against, so a
            // pending undo cannot outlive a load — Shell calls this on every return to the tab.
            ClearPendingUndo();

            // The catalogue's half of the load: the four queries, the two repairs, and the filter.
            await menu.LoadAsync();
            await autosave.RestoreAsync();

            /// <summary>The formatted amounts depend on Currencies.Default, which the operator can change on the Settings tab while this page's ViewModel instance is still alive — Shell keeps one page per tab, so coming back here re-runs LoadAsync on the SAME instance rather than building a new one. This refresh cannot be left to Recalculate. An empty cart totals 0 before and after a load, so SetProperty sees no change, raises nothing, and «Итого» would keep printing the old sign — measured on the emulator: tiles in ₿, cart total still in ₽. Hence the explicit re-raise of both the total and every cart row.</summary>

            RefreshMoneyText();

            /// <summary>…and the same argument applies to the fulfilment row's clock time. It is a computed reading of the wall clock (IsRequestedTimeLate), not a stored flag, so it goes stale on its own: an order promised for 14:20 stops being "time left" at 14:20 without anything happening. LoadAsync is the only thing that runs on every return to the tab, so it is where the marker is re-read. Nothing else on this page reads the time.</summary>

            OnPropertyChanged(nameof(RequestedTimeText));
            OnPropertyChanged(nameof(IsRequestedTimeLate));
            OnPropertyChanged(nameof(FulfilmentSummary));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the menu");
            SetError(exception, "Не удалось загрузить меню");
        }
        finally
        {
            IsBusy = false;
            loadGate.Release();
        }
    }

    private async Task AddProductAsync(Product? product)
    {
        if (product is null) return;
        if (!product.IsAvailable)
        {
            Message = "Это блюдо закончилось.";
            haptics.Warn();
            return;
        }

        try
        {
            // Which sheets open, when a cancel kills the add and which price applies are all
            // ProductAddFlow decisions. Only the two awaits and the cart mutation stay here.
            var flow = ProductAddFlow.Start(product);

            var modifierGroup = flow.ModifierGroup;
            var modifier = modifierGroup is null ? null : await modifierPicker.PickAsync(modifierGroup);
            if (flow.SubmitModifier(modifier) == AddToCartStepOutcome.Dismissed) return;

            string? variant = null;
            if (flow.RequiresVariantChoice)
            {
                // Every variant is sold out: there is nothing to show and no price to charge, so
                // the product cannot be added at all. Unlike a dismiss, the user is told why.
                if (!flow.CanOfferVariantChoice)
                {
                    Message = ProductAddFlow.NoAvailableVariantsMessage;
                    haptics.Warn();
                    return;
                }

                variant = await variantPicker.PickAsync(product.Name, flow.AvailableVariants);
                if (flow.SubmitVariant(variant) == AddToCartStepOutcome.Dismissed) return;
            }

            // A selected variant carries its own price and replaces the product price entirely.
            var effectivePrice = flow.ResolvePrice(variant);

            /// <summary>The merge is the CART's: it folds this line into an identical one already there, on the same OrderLineKey the cart rows, the order editor and OrderService all compute. An inline comparison is what this used to do, and it left the composition out of the identity — which merged two DIFFERENT builds of one bundle into one line and put one bundle in as two. Both cost money.</summary>

            cart.Add(new CartItemViewModel
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Price = effectivePrice,
                // A plain dish has no composition, so the allowed price IS the dish price and the
                // row has nothing struck through. That is the whole of the price control's first
                // signal for an ordinary line: only a hand edit can make the two differ.
                ListPrice = effectivePrice,
                SelectedModifierName = modifier,
                SelectedVariantName = variant,
                Quantity = 1
            });

            Message = string.Empty;
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add product {ProductId} to the cart", product.Id);
            SetError(exception, "Не удалось добавить блюдо");
        }
    }

    /// <summary>─ Bundles ──────────────────────────────────────────────────────────────────────────────── Nothing is here any more. The whole section - AddComboAsync, EditLineCompositionAsync, ResolveCompositionAsync, ReportBundleFailure, AddBundleLine - moved to CompositionResolver, because a bundle has exactly one way in: read it, refuse it if the shelf cannot fill it, compose it, and let the catalogue price it. AddProductAsync is deliberately NOT there - it is a different sheet, variants then modifiers - and neither is EditLinePriceAsync, which is a price override rather than a composition. ── Price override ────────────────────────────────────────────────────────────────────────────</summary>


    /// <summary>Re-prices one line by hand. The allowed price is left alone, so the row shows the allowed price struck through beside the changed one and the shift report's discount section finds the difference afterwards.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private async Task EditLinePriceAsync(CartItemViewModel? line)
    {
        if (line is null) return;

        var raw = await dialogs.PromptAsync(
            "Цена позиции",
            $"Цена за одну штуку. Сейчас {TextFormat.Money(line.Price)}.",
            Money.Round(line.Price).ToString("0.##"),
            "Сохранить",
            "Отмена");
        if (raw is null) return;

        if (!TextFormat.TryParseDecimal(raw, out var value) || value < 0)
        {
            Message = "Не удалось разобрать цену. Введите число, например 180 или 180,50.";
            haptics.Warn();
            return;
        }

        line.Price = Money.Round(value);
        Recalculate();
        Message = line.IsPriceOverridden
            ? $"«{line.ProductName}»: было {TextFormat.Money(line.ListPrice)}, стало {TextFormat.Money(line.Price)}."
            : $"«{line.ProductName}»: цена {TextFormat.Money(line.Price)}.";
        haptics.Click();
    }

    // ── Fulfilment, contact and time ───────────────────────────────────────────────────────────────

    /// <summary>Switches between eating in and taking away. Silent unless a typed phone was just destroyed.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private void SelectOrderType(OrderType value)
    {
        /// <summary>The DECISION is FulfilmentEditor's, down to the phone it reads before dropping and the silence it returns for a no-op. See FulfilmentSwitch for why those three are one thing and why silence means not touching Message at all. All this method has left is the two things the editor cannot do for itself: speak, and buzz.</summary>

        var change = fulfilment.Select(value);
        if (!change.Changed) return;

        /// <summary>Assigned ONLY on the loss. Setting Message to an empty string would still retire a pending undo (its setter calls ClearPendingUndo), so a silent switch must not touch the property at all rather than clearing it — «Отменить» armed by a removal has to survive a tap on the fulfilment button. Hence `is not null` and not `!= string.Empty`.</summary>

        if (change.Announce is not null) Message = change.Announce;
        haptics.Click();
    }

    /// <summary>The single fulfilment button: set whichever type the order is NOT.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private void SwitchOrderType() =>
        // The other type is the editor's to answer: it is the counterpart of the state it holds, and
        // spelling the comparison out here would give the shell a second copy of that rule.
        SelectOrderType(fulfilment.Other);

    /// <summary>Enters a phone, checks that it is one, and has the cashier read it back before it is kept.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private async Task EditPhoneAsync()
    {
        if (!IsTakeaway)
        {
            Message = "Телефон спрашивают только для заказа с собой.";
            return;
        }

        var draft = CustomerPhone ?? string.Empty;

        while (true)
        {
            var raw = await dialogs.PromptAsync(
                "Телефон клиента",
                "Номер, по которому можно дозвониться, когда заказ будет готов. С пробелами и скобками можно.",
                draft,
                "Проверить",
                "Отмена");

            // A dismissed prompt is not an edit: nothing changes, and nothing says it did.
            if (raw is null) return;

            if (string.IsNullOrWhiteSpace(raw))
            {
                fulfilment.SetPhone(null);
                Message = "Номер не указан. Заказ можно оформить без телефона.";
                return;
            }

            var normalized = PhoneNumber.Normalize(raw);
            if (normalized is null)
            {
                Message = "Это не похоже на номер: проверьте раскладку и лишние символы.";
                haptics.Warn();
                draft = raw;
                continue;
            }

            if (!PhoneNumber.IsValid(normalized))
            {
                Message = $"{normalized} — это не полный номер. Нужен номер целиком, например +7 916 123-45-67.";
                haptics.Warn();
                draft = raw;
                continue;
            }

            var confirmed = await dialogs.ConfirmAsync(
                "Проверьте номер",
                $"Записано: {normalized}\n\nПовторите номер покупателю вслух, прежде чем сохранить.",
                "Верно",
                "Ввести заново");

            if (!confirmed)
            {
                draft = raw;
                continue;
            }

            fulfilment.SetPhone(normalized);
            Message = "Номер сохранён.";
            haptics.Click();
            return;
        }
    }

    /// <summary>Opens the "когда" sheet: as soon as possible, or a clock time.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private async Task PickTimeAsync()
    {
        var choice = await timePicker.PickAsync("Когда приготовить", RequestedAt?.ToLocalTime().TimeOfDay);
        if (choice is null) return;

        if (choice.IsAsSoonAsPossible)
        {
            fulfilment.SetPromise(null);
            Message = OrderPromise.AsSoonAsPossibleMessage;
            haptics.Click();
            return;
        }

        var promised = OrderPromise.At(choice.TimeOfDay, timeProvider.GetLocalNow(), timeProvider.LocalTimeZone);
        fulfilment.SetPromise(promised);

        /// <summary>Overdue is stated, because it is not obvious from the figure. «Заказ к 14:20» at 15:05 is a different order from «Заказ к 14:20» at 13:00 and the sentence has to say which one this is — otherwise the operator reads their own confirmation as a mistake. Describing the LOCAL `promised` rather than re-reading the property: the sheet was opened on the order's current state, so a dismissal is a null and this is the only write. Reading it back would be a second source of truth for a value that is already in hand.</summary>

        Message = OrderPromise.Describe(promised, timeProvider.GetLocalNow(), timeProvider.LocalTimeZone);
        haptics.Click();
    }

    /// <summary>One step up on an existing line. The cart owns the quantity itself.</summary>
    private void AddItem(CartItemViewModel? item) => cart.StepUp(item);

    /// <summary>One step down, and the line away entirely at zero.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private void RemoveItem(CartItemViewModel? item) => cart.StepDown(item, message => Message = message);

    /// <summary>Recomputes the total from the lines. The cart owns the total.</summary>
    private void Recalculate() => cart.Recalculate();

    /// <summary>Re-raises every formatted amount on this screen — the cart total and each row's line total — after the selected currency may have changed.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(TotalText));
        // The checkout button's caption carries the formatted total inside it, so it has to be
        // re-announced here too - otherwise the button would quote a total in the old currency.
        OnPropertyChanged(nameof(PayAndCreateText));

        // The cart's rows and the bundle tiles, each through its own owner's refresh: the rows do not
        // flicker because they are re-raised rather than rebuilt, and the tiles need it at all
        // because their money is not produced by a converter.
        cart.RefreshMoneyText();
        menu.RefreshMoneyText();
    }
}
