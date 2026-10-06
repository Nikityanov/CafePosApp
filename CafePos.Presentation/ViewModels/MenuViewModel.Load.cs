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
        SelectedCategory = item?.Category;
        // A null item means the strip was cleared from under us (the category was deleted while the
        // page was closed) — fall back to «Все» rather than leaving the key on a chip that is gone.
        SelectedFilterKey = item?.Key ?? CategoryMenuItemViewModel.AllKey;
        HighlightSelectedChip();
        ApplyFilters();
    }

    /// <summary>Keeps the chip strip in sync with <see cref="SelectedFilterKey"/>.</summary>
    private void HighlightSelectedChip()
    {
        foreach (var chip in Categories)
            chip.IsSelected = chip.Key == SelectedFilterKey;
    }

    /// <summary>
    /// Rebuilds <see cref="FilteredProducts"/> from the selected category and the time window.
    /// Called by <see cref="SelectCategory"/> and by <see cref="LoadAsync"/>. There is no search
    /// on this page — the owner's call, see the row-structure comment in Views/MenuPage.xaml —
    /// so nothing else feeds it.
    /// </summary>
    private void ApplyFilters()
    {
        // On «Комбо» the grid is emptied outright rather than narrowed: a bundle is not a Product,
        // so there is no category id to match on and every comparison would be true at once.
        if (IsCombosOnly)
        {
            FilteredProducts.SyncWith([], product => product.Id);
            return;
        }

        var localHour = timeProvider.GetLocalNow().Hour;
        var query = Products.Where(product =>
            (SelectedCategory is null || product.CategoryId == SelectedCategory.Id)
            && ProductAvailability.IsInTimeWindow(product.AvailableFromHour, product.AvailableToHour, localHour));

        // Diff based filtering: no Clear(), so the list does not flicker or lose its scroll.
        FilteredProducts.SyncWith(query, product => product.Id);
    }

    public async Task LoadAsync()
    {
        await loadGate.WaitAsync();
        IsBusy = true;
        try
        {
            var products = await catalog.GetProductsAsync();
            var categories = await catalog.GetCategoriesAsync();

            // Whatever cart is restored below is not the cart an undo was armed against, so a
            // pending undo cannot outlive a load — Shell calls this on every return to the tab.
            ClearPendingUndo();

            Products.SyncWith(products, product => product.Id);

            await LoadCombosAsync();

            // The "Все" chip is the first element of the strip, so the whole row scrolls as one
            // list, and "Комбо" is the last: it is a filter over the other kind of content rather
            // than a section among these, so it belongs at the end where it reads as a switch.
            Categories.SyncWith(
                new[] { CategoryMenuItemViewModel.CreateAll() }
                    .Concat(categories.Select((category, index) => new CategoryMenuItemViewModel(category, index)))
                    .Append(CategoryMenuItemViewModel.CreateCombos()),
                item => item.Key);

            // The selected section may have been deleted while the page was closed. The two
            // pseudo-chips are keyed by sentinels that cannot collide with a real category, so a
            // key that is in neither set is a category that no longer exists and falls back to «Все».
            if (SelectedFilterKey != CategoryMenuItemViewModel.AllKey
                && SelectedFilterKey != CategoryMenuItemViewModel.CombosKey
                && Categories.All(item => item.Key != SelectedFilterKey))
            {
                SelectedFilterKey = CategoryMenuItemViewModel.AllKey;
            }
            SelectedCategory = Categories.FirstOrDefault(item => item.Key == SelectedFilterKey)?.Category;

            HighlightSelectedChip();
            ApplyFilters();
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
    /// Rebuilds the bundle row from the catalogue.
    /// </summary>
    /// <remarks>
    /// Refreshed rather than re-created, so the row's tile does not flicker on every return to the
    /// tab — the same reason the section chips are updated instead of rebuilt. The tiles are also the
    /// one place on this page whose text is money, so they are re-raised by
    /// <see cref="RefreshMoneyText"/> for the currency change.
    /// </remarks>
    private async Task LoadCombosAsync()
    {
        var templates = await combos.GetCombosAsync();
        var rows = templates.Select(BuildComboTile).ToList();
        Combos.SyncWith(rows, row => row.Id, (current, incoming) => current.Refresh(incoming));
    }

    /// <summary>
    /// Reads a catalogue bundle against what is on the shelf right now, and off the same slots the tile
    /// names in its composition line.
    /// </summary>
    private static MenuComboViewModel BuildComboTile(Combo template)
    {
        var plan = BundlePlan.Describe(template);
        return new MenuComboViewModel(
            template,
            template.PriceKopecks,
            plan.BlockedDishName,
            TextFormat.Money(Money.FromKopecks(template.PriceKopecks)),
            // Off the SAME components GetCombosAsync already loaded, not a query per tile: the names
            // were in hand to compute the price two lines above, and a second read would be N+1 on the
            // one screen the cashier looks at most. See ComboComposition.
            ComboComposition.Summarise(template.Components));
    }

    private async Task RestoreDraftAsync()
    {
        if (Cart.Count > 0) return;

        var snapshot = await drafts.LoadActiveCartAsync();
        if (snapshot.IsEmpty) return;

        // Through WithComponents so a restored bundle keeps its slots. A combo that arrives without
        // them prints as one line with nothing under it AND loses its merge signature, so it would
        // merge with an identical bundle and split from itself.
        foreach (var line in snapshot.Lines) Cart.Add(CartItemViewModel.FromLine(line).WithComponents(line.Components));
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

            // Merge through OrderLineKey rather than the inline product/modifier/variant comparison
            // this used to make. The key is the same function the cart rows, the order editor and
            // OrderService all compute, so the four cannot drift apart again — and it now carries the
            // composition, which the inline comparison could not: two different builds of one bundle
            // are two lines at two prices, and one bundle tapped twice is one line at quantity 2.
            var key = OrderLineKey.For(product.Id, modifier, variant);
            var existing = Cart.FirstOrDefault(item => item.MergeKey == key);

            if (existing is null)
            {
                Cart.Add(new CartItemViewModel
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
            }
            else
            {
                existing.Quantity++;
            }

            Recalculate();
            Message = string.Empty;
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add product {ProductId} to the cart", product.Id);
            SetError(exception, "Не удалось добавить блюдо");
        }
    }

    // ── Bundles ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the one composition sheet for a catalogue bundle and puts the result on the cart.
    /// </summary>
    /// <remarks>
    /// The same sheet, the same rows and the same confirm serve a custom build: a custom build is this
    /// one with «Очистить состав» pressed, not a second editor. Two editors is exactly where a shop
    /// grows the "bundles stopped showing in reports" bug, because only one of them feeds the sale.
    /// </remarks>
    private async Task AddComboAsync(MenuComboViewModel? tile)
    {
        if (tile is null) return;

        if (!tile.IsAvailable)
        {
            // Named, not generic. The sale-side refusal says the same thing, and the tile's whole
            // purpose is to get the cashier to the dish before they build the order.
            Message = $"Комбо «{tile.Name}» нельзя продать: {tile.UnavailableText}. Замените состав комбо в каталоге.";
            haptics.Warn();
            return;
        }

        try
        {
            var template = await combos.GetComboAsync(tile.Id);
            if (template is null)
            {
                Message = "Комбо больше нет в каталоге. Обновите меню и соберите заказ заново.";
                haptics.Warn();
                return;
            }

            var plan = BundlePlan.Describe(template);
            if (plan.BlockedDishName is not null)
            {
                // Read again rather than trusting the tile: the shelf changed since the last load.
                Message = $"Комбо «{template.Name}» нельзя продать: нет в наличии: {plan.BlockedDishName}. "
                          + "Замените состав комбо в каталоге.";
                haptics.Warn();
                return;
            }

            var chosen = await comboEditor.ComposeAsync(
                new ComboEditorRequest($"Состав комбо «{template.Name}»", plan.Options, plan.Selection, template.PriceKopecks));
            if (chosen is null) return;

            var resolved = await ResolveCompositionAsync(template, chosen);
            if (resolved is null) return;

            AddBundleLine(template.Id, template.Name, resolved, template.PriceKopecks);
            Recalculate();
            Message = string.Empty;
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add combo {ComboId} to the cart", tile.Id);
            ReportBundleFailure(exception, "Не удалось добавить комбо");
        }
    }

    /// <summary>
    /// Re-opens the composition sheet for a bundle already on the cart and writes the result back.
    /// </summary>
    /// <remarks>
    /// Editing a line's composition does NOT reprice it — the bundle's own price is a number in the
    /// card, and the sum of the slots is only the à la carte reference. A hand-set price does NOT
    /// survive the change and the message says so: the override is a decision about one specific
    /// bundle, and carrying it silently onto a different one is how an override becomes a discount
    /// nobody chose.
    /// </remarks>
    private async Task EditLineCompositionAsync(CartItemViewModel? line)
    {
        if (line is null || !line.IsCombo) return;

        try
        {
            var template = await combos.GetComboAsync(line.ProductId);
            if (template is null)
            {
                Message = "Комбо больше нет в каталоге. Уберите позицию из чека.";
                haptics.Warn();
                return;
            }

            var plan = BundlePlan.Describe(template);
            if (plan.BlockedDishName is not null)
            {
                Message = $"Комбо «{template.Name}» нельзя продать: нет в наличии: {plan.BlockedDishName}.";
                haptics.Warn();
                return;
            }

            // Pre-filled from the LINE, not from the catalogue: the cashier may already have built this
            // one differently, and reopening the sheet must show what is actually on the cart.
            var chosen = await comboEditor.ComposeAsync(new ComboEditorRequest(
                $"Состав комбо «{line.ProductName}»",
                plan.Options,
                line.Components.Select(component =>
                    new ComboSlotChoice(component.ProductId, component.QuantityPerUnit)).ToList(),
                template.PriceKopecks));
            if (chosen is null) return;

            var resolved = await ResolveCompositionAsync(template, chosen);
            if (resolved is null) return;

            var wasOverridden = line.IsPriceOverridden;
            var previous = line.Price;

            line.Components.Clear();
            foreach (var component in LineComponentViewModel.FromLine(resolved)) line.Components.Add(component);

            // The bundle's OWN PRICE, not the sum of its parts. Changing the composition does not
            // reprice the bundle — the price is a number in the card, and the sum is only the
            // à la carte reference the discount is measured against.
            var price = Money.FromKopecks(template.PriceKopecks);

            line.ListPrice = price;
            line.Price = price;
            line.OnCompositionChanged();

            Recalculate();
            Message = wasOverridden
                ? $"Состав «{line.ProductName}» изменён, цена пересчитана: {TextFormat.Money(previous)} → {TextFormat.Money(price)}."
                : $"Состав «{line.ProductName}» изменён, итог {TextFormat.Money(price)}.";
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to edit the composition of cart line {ProductId}", line.ProductId);
            ReportBundleFailure(exception, "Не удалось изменить состав комбо");
        }
    }

    /// <summary>
    /// Hands a requested composition to the catalogue and takes back the one that may be sold.
    /// </summary>
    /// <returns>The resolved slots, or <c>null</c> when the catalogue refused — already reported.</returns>
    /// <remarks>
    /// The probe line carries the cart's own product identifiers and <b>no prices of its own</b>, which
    /// is the point: <see cref="IComboService.ResolveSaleCompositionsAsync"/> rebuilds every name and both
    /// prices from the catalogue, so a client cannot compose a bundle whose sum agrees with whatever it
    /// was about to charge. The price control compares against exactly these rows.
    /// <para>
    /// The bundle's own price comes back in the <see cref="SaleComposition"/> too, and the caller takes
    /// it from there — the client does not decide the price, the catalogue does. Until this called
    /// <see cref="IComboService.ResolveSaleComponentsAsync"/> and the caller priced the line from the
    /// sum of the returned slots, which meant a dearer substitute reached the customer on the client
    /// side even though the server flags the line as an override.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CheckoutComponent>?> ResolveCompositionAsync(
        Combo template,
        IReadOnlyList<ComboSlotChoice> chosen)
    {
        var probe = new CheckoutLine(
            template.Id,
            template.Name,
            0m,
            1,
            null,
            null,
            chosen.Select(choice => new CheckoutComponent(choice.ProductId, template.Name, choice.QuantityPerUnit, 0, 0)).ToList());

        try
        {
            var resolved = await combos.ResolveSaleCompositionsAsync([probe]);
            return resolved.Count > 0 ? resolved[0].Components : [];
        }
        catch (Exception exception)
        {
            ReportBundleFailure(exception, "Комбо нельзя продать");
            return null;
        }
    }

    /// <summary>
    /// Reports a bundle refusal with the domain's own wording and NOTHING in front of it.
    /// </summary>
    /// <remarks>
    /// A <see cref="ValidationFailureException"/> from the bundle path NAMES THE DISH — that is what the
    /// whole message is for, and it is written by the domain. Prefixing it with "Не удалось добавить
    /// комбо:" buries the dish under a sentence about the app, and replacing it with a generic error
    /// sends the cashier through four slots of the bundle to find the one that ran out. So the message
    /// is shown as written; only the flag marks it as a failure.
    /// </remarks>
    private void ReportBundleFailure(Exception exception, string prefix)
    {
        if (exception is ValidationFailureException)
        {
            Message = exception.Message;
            IsErrorMessage = true;
            return;
        }

        SetError(exception, prefix);
    }

    /// <summary>Adds one bundle line, or tops the identical one up — the merge key decides which.</summary>
    /// <param name="priceKopecks">
    /// The bundle's own price, in kopecks — <see cref="Combo.PriceKopecks"/> from the catalogue, not
    /// the sum of the returned slots. The sum is the à la carte reference; the price is what the
    /// till charges.
    /// </param>
    private void AddBundleLine(Guid comboId, string comboName, IReadOnlyList<CheckoutComponent> components, long priceKopecks)
    {
        var price = Money.FromKopecks(priceKopecks);

        var key = OrderLineKey.For(
            comboId,
            null,
            null,
            components.Select(component => (component.ProductId, component.QuantityPerUnit)));

        var existing = Cart.FirstOrDefault(item => item.MergeKey == key);
        if (existing is not null)
        {
            existing.Quantity++;
            return;
        }

        var line = new CartItemViewModel
        {
            ProductId = comboId,
            ProductName = comboName,
            // The allowed price and the charged price are the same number here: nothing has been
            // overridden yet, and the sheet's re-price only ever writes one of them. The distinction
            // starts existing when the cashier edits the price by hand.
            Price = price,
            ListPrice = price,
            Quantity = 1
        };

        foreach (var component in LineComponentViewModel.FromLine(components)) line.Components.Add(component);
        Cart.Add(line);
    }

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
        if (OrderType == value) return;

        // Read BEFORE the drop below. After `CustomerPhone = null` there is nothing left to ask about,
        // which is the whole reason the guard has to come first.
        var dropsPhone = value == OrderType.CounterService && !string.IsNullOrWhiteSpace(CustomerPhone);

        OrderType = value;
        if (value == OrderType.CounterService) CustomerPhone = null;

        // Assigned ONLY on the loss. Setting Message to an empty string would still retire a pending
        // undo (its setter calls ClearPendingUndo), so a silent switch must not touch the property at
        // all rather than clearing it — «Отменить» armed by a removal has to survive a tap on the
        // fulfilment button.
        if (dropsPhone) Message = "Телефон удалён: заказ в зале, номер не сохраняем.";
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
        SelectOrderType(OrderType == OrderType.CounterService ? OrderType.Takeaway : OrderType.CounterService);

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
                CustomerPhone = null;
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

            CustomerPhone = normalized;
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
            RequestedAt = null;
            Message = "Как можно скорее — время по умолчанию.";
            haptics.Click();
            return;
        }

        RequestedAt = ResolveRequestedTime(choice.TimeOfDay);

        // Overdue is stated, because it is not obvious from the figure. «Заказ к 14:20» at 15:05 is a
        // different order from «Заказ к 14:20» at 13:00 and the sentence has to say which one this is —
        // otherwise the operator reads their own confirmation as a mistake.
        Message = IsRequestedTimeLate
            ? $"Заказ к {RequestedTimeText} — время уже прошло, заказ просрочен."
            : $"Заказ к {RequestedTimeText}.";
        haptics.Click();
    }

    /// <summary>
    /// The clock time the cashier picked, placed on TODAY at that wall-clock time.
    /// </summary>
    /// <remarks>
    /// <b>AND A TIME THAT HAS ALREADY PASSED STAYS ON TODAY.</b> It used to roll forward to tomorrow,
    /// which silently rewrote the operator's decision: they picked 14:20 at 15:00 and got an order
    /// promised for tomorrow, which is not what anyone said, and which also made an OVERDUE order
    /// inexpressible — the board knows how to show one (<c>Order.IsScheduledAt</c> is false for a past
    /// <c>RequestedAt</c>), and there was no way to create it. A customer who said «в 14:20» and is
    /// still waiting at 15:00 is the ordinary reason this control exists. So the time is taken at
    /// face value, the sheet marks it
    /// (<c>TimePickerPopup.LateNoteLabel</c>) and the cart row carries it
    /// (<see cref="IsRequestedTimeLate"/>), and nothing refuses it.
    /// <para>
    /// Built through <see cref="DateTimeOffset(DateTime)"/> on an unspecified-kind value so the local
    /// offset is resolved for that wall-clock time rather than for now: across a daylight-saving change
    /// the two differ by an hour, and an order promised for a time that does not exist on its day is an
    /// order nobody can keep.
    /// </para>
    /// </remarks>
    private DateTimeOffset ResolveRequestedTime(TimeSpan timeOfDay)
    {
        var now = timeProvider.GetLocalNow();
        var target = DateTime.SpecifyKind(now.DateTime.Date + timeOfDay, DateTimeKind.Unspecified);
        return new DateTimeOffset(target);
    }

    private void AddItem(CartItemViewModel? item)
    {
        if (item is null) return;
        // Adding after removing is the operator moving on, so the pending undo is retired. It would
        // also die on the next message; this makes it immediate rather than merely eventual.
        ClearPendingUndo();
        item.Quantity++;
        Recalculate();
    }

    /// <summary>
    /// One step down, and the line away entirely at zero.
    /// </summary>
    /// <remarks>
    /// Only the step that removes the LINE arms an undo. Stepping 3 down to 2 is not a mistake
    /// worth a control — the cashier can step back up — whereas a line leaving the cart is the one
    /// edit on this screen with no visible way back, and NN/g's finding that users "accidentally
    /// added the same item to their cart multiple times" is the same failure seen from the other
    /// side: an unintended edit here is corrected by an affordance, not by another tap on the line
    /// that is no longer there.
    /// <para>
    /// The message is written BEFORE the undo is armed, because Message's setter is what retires the
    /// previous one. Written the other way round, this line's own undo would be cancelled by its own
    /// message and «Отменить» would never appear.
    /// </para>
    /// </remarks>
    private void RemoveItem(CartItemViewModel? item)
    {
        if (item is null) return;
        item.Quantity--;
        if (item.Quantity <= 0)
        {
            var index = Cart.IndexOf(item);
            Message = $"{item.ProductName} — убрано из корзины.";
            // The count BEFORE the decrement, so the undo can put the line back as it was rather
            // than as a zero-quantity husk. See MenuViewModel.UndoRemove for what that cost.
            pendingUndo = new PendingUndo(item, index, item.Quantity + 1);
            OnPropertyChanged(nameof(HasUndo));
            Cart.Remove(item);
        }

        Recalculate();
    }

    private void Recalculate() => Total = Cart.Sum(item => item.LineTotal);

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
        // re-announced here too — otherwise the button would quote a total in the old currency.
        OnPropertyChanged(nameof(PayAndCreateText));
        foreach (var item in Cart) item.RefreshMoneyText();

        // The bundle tiles carry money too, and they are the one row on this page whose text is not
        // produced by a converter (a tile knows its price from the composition, not from a Product), so
        // nothing else re-raises them when the operator switches currency on the Settings tab.
        foreach (var combo in Combos) combo.RefreshMoneyText();
    }
}
