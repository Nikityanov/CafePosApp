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
            await RestoreDraftAsync();

            // The formatted amounts depend on Currencies.Default, which the operator can change on
            // the Settings tab while this page's ViewModel instance is still alive — Shell keeps one
            // page per tab, so coming back here re-runs LoadAsync on the SAME instance rather than
            // building a new one.
            //
            // This refresh cannot be left to Recalculate. An empty cart totals 0 before and after a
            // load, so SetProperty sees no change, raises nothing, and «Итого» would keep printing the
            // old sign — measured on the emulator: tiles in ₿, cart total still in ₽. Hence the
            // explicit re-raise of both the total and every cart row.
            RefreshMoneyText();

            // …and the same argument applies to the fulfilment row's clock time. It is a computed
            // reading of the wall clock (IsRequestedTimeLate), not a stored flag, so it goes stale on
            // its own: an order promised for 14:20 stops being "time left" at 14:20 without anything
            // happening. LoadAsync is the only thing that runs on every return to the tab, so it is
            // where the marker is re-read. Nothing else on this page reads the time.
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

    /// <summary>
    private async Task RestoreDraftAsync()
    {
        if (Cart.Count > 0) return;

        var snapshot = await drafts.LoadActiveCartAsync();
        if (snapshot.IsEmpty) return;

        // Through WithComponents so a restored bundle keeps its slots. A combo that arrives without
        // them prints as one line with nothing under it AND loses its merge signature, so it would
        // merge with an identical bundle and split from itself.
        cart.RestoreLines([.. snapshot.Lines.Select(line => CartItemViewModel.FromLine(line).WithComponents(line.Components))]);
        Recalculate();
        // The header continuation, not the message strip — see DraftNotice. The word
        // «Восстановлен» was dropped on the owner's instruction: the header already says
        // «Корзина», so the note reads as a property of the cart rather than as a report
        // of an action nobody performed in this session.
        DraftNotice = snapshot.SavedAt is { } savedAt
            ? $"несохранённый чек от {savedAt.ToLocalTime():HH:mm}"
            : "несохранённый чек";
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

            // The merge is the CART's: it folds this line into an identical one already there, on the same
            // OrderLineKey the cart rows, the order editor and OrderService all compute. An inline
            // comparison is what this used to do, and it left the composition out of the identity —
            // which merged two DIFFERENT builds of one bundle into one line and put one bundle in as
            // two. Both cost money.
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

    // ─ Bundles ────────────────────────────────────────────────────────────────────────────────
    // Nothing is here any more. The whole section - AddComboAsync, EditLineCompositionAsync,
    // ResolveCompositionAsync, ReportBundleFailure, AddBundleLine - moved to CompositionResolver,
    // because a bundle has exactly one way in: read it, refuse it if the shelf cannot fill it,
    // compose it, and let the catalogue price it. AddProductAsync is deliberately NOT there - it is
    // a different sheet, variants then modifiers - and neither is EditLinePriceAsync, which is a
    // price override rather than a composition.
    // ── Price override ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-prices one line by hand. The allowed price is left alone, so the row shows the allowed price
    /// struck through beside the changed one and the shift report's discount section finds the
    /// difference afterwards.
    /// </summary>
    /// <remarks>
    /// <b>THERE IS NO REASON FIELD, AND THERE WILL NOT BE ONE.</b> A reason collected at the till becomes
    /// the first value anyone ever clicks and adds no control at all; only the aggregate pattern of
    /// overrides is worth anything, and the report already has it. Nothing is asked here beyond the
    /// number, and nothing is refused — a POS that argues with a cashier about a price trains people to
    /// work around it, which is worse than a discount a manager reads about afterwards.
    /// </remarks>
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

    /// <summary>
    /// Switches between eating in and taking away. <b>Silent unless a typed phone was just
    /// destroyed.</b>
    /// </summary>
    /// <remarks>
    /// Switching to counter service DROPS the phone rather than hiding the row. That is the legal
    /// shape of the rule, not a tidiness one: 152-ФЗ ст. 6(1)(5) allows a phone to be processed only
    /// where it is needed to perform the contract, and a customer on the premises is found in no way
    /// at all. Carrying a number invisibly into a checkout that will drop it anyway would mean the
    /// value existed with no purpose behind it — the redundant personal data ст. 5(4)-(5) is about —
    /// and it would survive in the parked cart too.
    /// <para>
    /// <b>WHY NO MESSAGE ON A ROUTINE SWITCH.</b> This used to announce both directions every time
    /// («Заказ с собой: телефон нужен…» / «Заказ в зале: телефон не спрашиваем…»). That was the
    /// right message when the control was a two-segment toggle whose two segments restated the state;
    /// the toggle is one button now, so the state is already on screen in two places — the collapsed
    /// row reads «В зале · готово сейчас», and the button reads the change it makes — and a third
    /// restatement pushed the message strip 100px down while stating nothing the operator could not
    /// see. The strip earns its height by carrying things the screen does NOT already show; a
    /// confirmation of what the row just said is not one of them. It is still the right home for
    /// validation and stock failures, and nothing here touches that: only <see cref="Message"/>'s
    /// assignment is gone, never the strip.
    /// </para>
    /// <para>
    /// <b>THE ONE EXCEPTION, AND IT IS NOT A ROUTINE SWITCH.</b> Data the operator typed is being
    /// deleted underneath them. That is not a state they can read off a row — the phone row vanishes
    /// in the same frame — so it is said out loud, and the wording names the loss rather than the
    /// policy. A switch made with <b>no</b> phone typed destroys nothing, so it stays silent: the same
    /// sentence for both directions would be noise on the common tap and would train the operator to
    /// read past the strip.
    /// </para>
    /// </remarks>
    private void SelectOrderType(OrderType value)
    {
        // The DECISION is FulfilmentEditor's, down to the phone it reads before dropping and the
        // silence it returns for a no-op. See FulfilmentSwitch for why those three are one thing and
        // why silence means not touching Message at all. All this method has left is the two things
        // the editor cannot do for itself: speak, and buzz.
        var change = fulfilment.Select(value);
        if (!change.Changed) return;

        // Assigned ONLY on the loss. Setting Message to an empty string would still retire a pending
        // undo (its setter calls ClearPendingUndo), so a silent switch must not touch the property at
        // all rather than clearing it — «Отменить» armed by a removal has to survive a tap on the
        // fulfilment button. Hence `is not null` and not `!= string.Empty`.
        if (change.Announce is not null) Message = change.Announce;
        haptics.Click();
    }

    /// <summary>
    /// The single fulfilment button: set whichever type the order is NOT.
    /// </summary>
    /// <remarks>
    /// One command and one target instead of a two-segment toggle. It deliberately goes through
    /// <see cref="SelectOrderType"/> rather than assigning <see cref="OrderType"/> directly, because
    /// every consequence of that decision lives in there — the phone row appearing and disappearing,
    /// the dropped number, the one message that is worth sending — and bypassing it is how a state
    /// change ends up on screen without them. <c>SelectOrderType</c>'s no-op guard makes a second press
    /// a no-op, which is what a toggle wants.
    /// </remarks>
    private void SwitchOrderType() =>
        // The other type is the editor's to answer: it is the counterpart of the state it holds, and
        // spelling the comparison out here would give the shell a second copy of that rule.
        SelectOrderType(fulfilment.Other);

    /// <summary>
    /// Enters a phone, checks that it is one, and has the cashier read it back before it is kept.
    /// </summary>
    /// <remarks>
    /// <b>VALIDATE, THEN CONFIRM BACK — TWO CHECKS DOING TWO JOBS.</b> Frontol 6 asks the operator to
    /// check the number for correctness as it is entered and then to "check the data once more
    /// <i>with the customer</i>", confirming or changing it: the first catches a mistyped digit, the
    /// second takes away any reason to look it up in a phonebook instead of asking, and together they
    /// are why the customer is not left hunting in a notebook. The confirm keeps the number on screen
    /// for as long as the question is there, which is what makes reading it back possible at all.
    /// <para>
    /// The confirm prints the number IN FULL, which is the one place on a customer-facing screen where
    /// an unmasked number appears — and it has to, because a masked number cannot be checked against
    /// what the customer is saying. The cart's own line stays masked
    /// (<see cref="PhoneDisplayText"/>); the full number lives only on the staff-facing
    /// <c>OrderDetailsPage</c>.
    /// </para>
    /// <para>
    /// A blank answer clears the number rather than failing: a customer who declines to give one is a
    /// normal outcome, and an order without a contact is a valid takeaway order.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Opens the "когда" sheet: as soon as possible, or a clock time.
    /// </summary>
    /// <remarks>
    /// The sheet opens on the order's current state, so it is a statement about the order rather than a
    /// blank question — and a dismissal changes nothing at all, which is what a null result means all
    /// the way down to <see cref="RequestedAt"/>.
    /// </remarks>
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

        // Overdue is stated, because it is not obvious from the figure. «Заказ к 14:20» at 15:05 is a
        // different order from «Заказ к 14:20» at 13:00 and the sentence has to say which one this is —
        // otherwise the operator reads their own confirmation as a mistake.
        //
        // Describing the LOCAL `promised` rather than re-reading the property: the sheet was opened on
        // the order's current state, so a dismissal is a null and this is the only write. Reading it
        // back would be a second source of truth for a value that is already in hand.
        Message = OrderPromise.Describe(promised, timeProvider.GetLocalNow(), timeProvider.LocalTimeZone);
        haptics.Click();
    }

    /// <summary>One step up on an existing line. The cart owns the quantity itself.</summary>
    private void AddItem(CartItemViewModel? item) => cart.StepUp(item);

    /// <summary>
    /// One step down, and the line away entirely at zero.
    /// </summary>
    /// <remarks>
    /// The step is <see cref="CartBuilder.StepDown"/>'s, and so is the order it does two things in: it
    /// announces FIRST and arms the undo second, because <c>Message</c>'s setter retires the previous
    /// undo. That ordering is passed in here as <see cref="Message"/> rather than left to the caller
    /// to respect, because a caller that gets it backwards loses the undo silently.
    /// </remarks>
    private void RemoveItem(CartItemViewModel? item) => cart.StepDown(item, message => Message = message);

    /// <summary>Recomputes the total from the lines. The cart owns the total.</summary>
    private void Recalculate() => cart.Recalculate();

    /// <summary>
    /// Re-raises every formatted amount on this screen — the cart total and each row's line total —
    /// after the selected currency may have changed.
    /// </summary>
    /// <remarks>
    /// Called at the end of <see cref="LoadAsync"/>, which Shell drives on every return to the tab.
    /// The per-item properties are raised through <see cref="CartItemViewModel"/>'s own
    /// notification rather than by rebuilding the rows, so the cart does not flicker or lose its
    /// scroll position when nothing about the money itself actually changed.
    /// </remarks>
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
