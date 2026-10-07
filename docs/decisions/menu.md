# Меню и корзина

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## menu

```csharp
private readonly MenuCatalogue menu;
```

The shell keeps `Products`, `FilteredProducts`, `Categories`
and `Combos` as one-line proxies because XAML binds them here by name, and the
catalogue owns them. `SelectedCategory` and `IsCombosOnly` are computed
from the catalogue's key, which is why the shell has no filter state of its own.

## cart

```csharp
private readonly CartBuilder cart;
```

The shell keeps `Cart`, `Total` and `TotalText` as one-line
proxies because XAML binds them here by name, and keeps `PayAndCreateText` and
`CanCreateOrder` outright because they quote the total inside a control that also
depends on `IsBusy`, which is the shell's. The subscription in the constructor is
what keeps those two honest: without it the checkout button would go on quoting the amount the
cart had before the change.

## composition

```csharp
private readonly CompositionResolver composition;
```

Two commands hand their handlers straight to it, so the shell has no bundle method of its own
and nothing to keep in step. It is constructed before those commands because the lambdas close
over it — the same ordering rule as the other three, and see the note above for why.

## Combos

```csharp
public ObservableCollection<MenuComboViewModel> Combos => menu.Combos;
```

A separate row is the correct shape, not a fallback: `Combo` has no category, so
folding bundles into the product grid would put them under whatever section chip happened to be
selected — or hide every bundle the moment one was. They have no section, so they get no
section filter, and a row of their own is where that reads correctly. It also leaves the
product grid's measured column-width contract (see
`MenuViewModel.ProductColumnSpan` and the GridItemsLayout comment in MenuPage.xaml)
exactly as it was, instead of making a bundle tile fight a product tile for a 140dp column.

## PayAndCreateText

```csharp
public string PayAndCreateText => $"Оплатить {TotalText}";
```

**Why the figure is inside the button and not beside it.** The owner's decision, over
placing the total in the row to the left of the button: both cost the same row, and this leaves
one focus point instead of a figure and a verb the operator has to associate before pressing
anything. A button that NAMES what it charges also answers the question Baymard's 2024 study
recorded a tester asking verbatim — «I'm not sure if I click the 'Next' button, will it
charge?» — which is a question about this exact control.
The «Итого» row this replaced is gone from the markup rather than hidden, and the separator
above the footer stayed: what it divided (the cart lines from the footer) still exists.

## DraftNotice

```csharp
public string DraftNotice => autosave.DraftNotice;
```

The state is DraftAutosave's; this is a read, because MenuPage.xaml binds the pair by name
and the header alternates between this and the word «Корзина» depending on it. The long
reasoning about why it is not a message moved with it.

## HasUndo

```csharp
public bool HasUndo => cart.HasUndo;
```

A proxy, like `Cart` and `Total`. The state itself is
`CartBuilder`'s; the shell re-announces it because the strip that shows it is
bound here.

## UndoRemove

```csharp
private void UndoRemove()
```

The restore itself is `CartBuilder.Restore`'s, down to the quantity and the index —
the two things that cost an emulator session when they were wrong. What stays here is the two
things the cart must not know about: the sentence, and the haptic.

## MessageColor

```csharp
public Color MessageColor => IsErrorMessage
```

This label was MEASURED wrong on a Pixel 7 in dark theme: it painted
`Success #2E7D32` on `#1E1E1E`, 3.28:1, because the colour arrived as a
`Setter` inside a `DataTrigger` holding a bare `{StaticResource Success}` —
and a trigger Setter takes a VALUE, not a binding expression, so the token is resolved
once at parse time against the light theme and no later theme change can re-resolve it.
Two triggers, one per outcome, so both branches were latched.
The fix is to stop using a trigger for a colour at all and resolve it here, against the
live theme — `SuccessDark` measures 8.28:1 on `SurfaceDark`, which is the figure
the measured 3.28:1 should have been.
Both outcomes carry their own token, so unlike the untinted case elsewhere there is no
"leave it to the implicit style" branch to reproduce. The text always says which one it is
("Заказ #1 создан…", "Добавьте товары в заказ."), so the colour reinforces the sentence and
never carries the meaning alone.

## fulfilment

```csharp
private readonly FulfilmentEditor fulfilment;
```

The third Collaborator. Every member below is a proxy onto
`FulfilmentEditor` because MenuPage.xaml binds them here by name; the state is
the editor's and the values are not mirrored. The constructor subscribes and re-announces, so
a binding reads the editor's value rather than a stale copy.

## PhoneDisplayText

```csharp
public string PhoneDisplayText => fulfilment.PhoneDisplayText;
```

The cart is the customer-facing surface, so the mask goes here and nowhere else on this screen.

## IsRequestedTimeLate

```csharp
public bool IsRequestedTimeLate => fulfilment.IsRequestedTimeLate;
```

A computed reading of the wall clock, not a stored flag, so it cannot disagree with the order —
and therefore goes stale on its own, which is why `LoadAsync` re-reads it on every return to
the tab.

## ToggleOrderTypeCommand

```csharp
public IRelayCommand ToggleOrderTypeCommand { get; }
```

Replaces `SelectCounterServiceCommand` and `SelectTakeawayCommand`, which existed only
to back the two segments. It calls the same `SelectOrderType` either way, so the legal and
privacy consequences still run exactly as they did — a switch back to counter service still
DROPS the phone rather than hiding the row (152-ФЗ ст. 6(1)(5)). One command also means there is
no pair that can disagree about which state is live: the button reads
`ToggleOrderTypeText`, which is derived from the same `OrderType`.
Neither direction writes to the message strip any more, because the state it used to announce is
now on screen twice already — and a switch that DOES destroy a typed phone still does announce
itself, since that loss is not readable off a row. See `SelectOrderType` for both halves.

## ToggleFulfilmentCommand

```csharp
public IRelayCommand ToggleFulfilmentCommand { get; }
```

A toggle rather than two commands, so the control that is on screen and the state it moves
cannot disagree: there is no «Открыть» button that stays live after the block is already open.

## EditLinePriceAsync

```csharp
private async Task EditLinePriceAsync(CartItemViewModel? line)
```

**THERE IS NO REASON FIELD, AND THERE WILL NOT BE ONE.** A reason collected at the till becomes
the first value anyone ever clicks and adds no control at all; only the aggregate pattern of
overrides is worth anything, and the report already has it. Nothing is asked here beyond the
number, and nothing is refused — a POS that argues with a cashier about a price trains people to
work around it, which is worse than a discount a manager reads about afterwards.

## SelectOrderType

```csharp
private void SelectOrderType(OrderType value)
```

Switching to counter service DROPS the phone rather than hiding the row. That is the legal
shape of the rule, not a tidiness one: 152-ФЗ ст. 6(1)(5) allows a phone to be processed only
where it is needed to perform the contract, and a customer on the premises is found in no way
at all. Carrying a number invisibly into a checkout that will drop it anyway would mean the
value existed with no purpose behind it — the redundant personal data ст. 5(4)-(5) is about —
and it would survive in the parked cart too.
**WHY NO MESSAGE ON A ROUTINE SWITCH.** This used to announce both directions every time
(«Заказ с собой: телефон нужен…» / «Заказ в зале: телефон не спрашиваем…»). That was the
right message when the control was a two-segment toggle whose two segments restated the state;
the toggle is one button now, so the state is already on screen in two places — the collapsed
row reads «В зале · готово сейчас», and the button reads the change it makes — and a third
restatement pushed the message strip 100px down while stating nothing the operator could not
see. The strip earns its height by carrying things the screen does NOT already show; a
confirmation of what the row just said is not one of them. It is still the right home for
validation and stock failures, and nothing here touches that: only `Message`'s
assignment is gone, never the strip.
**THE ONE EXCEPTION, AND IT IS NOT A ROUTINE SWITCH.** Data the operator typed is being
deleted underneath them. That is not a state they can read off a row — the phone row vanishes
in the same frame — so it is said out loud, and the wording names the loss rather than the
policy. A switch made with **no** phone typed destroys nothing, so it stays silent: the same
sentence for both directions would be noise on the common tap and would train the operator to
read past the strip.

## SwitchOrderType

```csharp
private void SwitchOrderType() =>
```

One command and one target instead of a two-segment toggle. It deliberately goes through
`SelectOrderType` rather than assigning `OrderType` directly, because
every consequence of that decision lives in there — the phone row appearing and disappearing,
the dropped number, the one message that is worth sending — and bypassing it is how a state
change ends up on screen without them. `SelectOrderType`'s no-op guard makes a second press
a no-op, which is what a toggle wants.

## EditPhoneAsync

```csharp
private async Task EditPhoneAsync()
```

**VALIDATE, THEN CONFIRM BACK — TWO CHECKS DOING TWO JOBS.** Frontol 6 asks the operator to
check the number for correctness as it is entered and then to "check the data once more
<i>with the customer</i>", confirming or changing it: the first catches a mistyped digit, the
second takes away any reason to look it up in a phonebook instead of asking, and together they
are why the customer is not left hunting in a notebook. The confirm keeps the number on screen
for as long as the question is there, which is what makes reading it back possible at all.
The confirm prints the number IN FULL, which is the one place on a customer-facing screen where
an unmasked number appears — and it has to, because a masked number cannot be checked against
what the customer is saying. The cart's own line stays masked
(`PhoneDisplayText`); the full number lives only on the staff-facing
`OrderDetailsPage`.
A blank answer clears the number rather than failing: a customer who declines to give one is a
normal outcome, and an order without a contact is a valid takeaway order.

## PickTimeAsync

```csharp
private async Task PickTimeAsync()
```

The sheet opens on the order's current state, so it is a statement about the order rather than a
blank question — and a dismissal changes nothing at all, which is what a null result means all
the way down to `RequestedAt`.

## RemoveItem

```csharp
private void RemoveItem(CartItemViewModel? item) => cart.StepDown(item, message => Message = message);
```

The step is `CartBuilder.StepDown`'s, and so is the order it does two things in: it
announces FIRST and arms the undo second, because `Message`'s setter retires the previous
undo. That ordering is passed in here as `Message` rather than left to the caller
to respect, because a caller that gets it backwards loses the undo silently.

## RefreshMoneyText

```csharp
private void RefreshMoneyText()
```

Called at the end of `LoadAsync`, which Shell drives on every return to the tab.
The per-item properties are raised through `CartItemViewModel`'s own
notification rather than by rebuilding the rows, so the cart does not flicker or lose its
scroll position when nothing about the money itself actually changed.

## RefreshMoneyText

```csharp
RefreshMoneyText();
```

The formatted amounts depend on Currencies.Default, which the operator can change on the Settings tab while this page's ViewModel instance is still alive — Shell keeps one page per tab, so coming back here re-runs LoadAsync on the SAME instance rather than building a new one. This refresh cannot be left to Recalculate. An empty cart totals 0 before and after a load, so SetProperty sees no change, raises nothing, and «Итого» would keep printing the old sign — measured on the emulator: tiles in ₿, cart total still in ₽. Hence the explicit re-raise of both the total and every cart row.

## OnPropertyChanged

```csharp
OnPropertyChanged(nameof(RequestedTimeText));
```

…and the same argument applies to the fulfilment row's clock time. It is a computed reading of the wall clock (IsRequestedTimeLate), not a stored flag, so it goes stale on its own: an order promised for 14:20 stops being "time left" at 14:20 without anything happening. LoadAsync is the only thing that runs on every return to the tab, so it is where the marker is re-read. Nothing else on this page reads the time.

## Add

```csharp
cart.Add(new CartItemViewModel
```

The merge is the CART's: it folds this line into an identical one already there, on the same OrderLineKey the cart rows, the order editor and OrderService all compute. An inline comparison is what this used to do, and it left the composition out of the identity — which merged two DIFFERENT builds of one bundle into one line and put one bundle in as two. Both cost money.

## summary

```csharp
/// <summary>Re-prices one line by hand. The allowed price is left alone, so the row shows the allowed price struck through beside the changed one and the shift report's discount section finds the difference afterwards.</summary>
```

─ Bundles ──────────────────────────────────────────────────────────────────────────────── Nothing is here any more. The whole section - AddComboAsync, EditLineCompositionAsync, ResolveCompositionAsync, ReportBundleFailure, AddBundleLine - moved to CompositionResolver, because a bundle has exactly one way in: read it, refuse it if the shelf cannot fill it, compose it, and let the catalogue price it. AddProductAsync is deliberately NOT there - it is a different sheet, variants then modifiers - and neither is EditLinePriceAsync, which is a price override rather than a composition. ── Price override ────────────────────────────────────────────────────────────────────────────

## remarks

```csharp
/// <remarks>Почему так — `docs/decisions/menu.md`</remarks>
```

Re-prices one line by hand. The allowed price is left alone, so the row shows the allowed price struck through beside the changed one and the shift report's discount section finds the difference afterwards.

## change

```csharp
var change = fulfilment.Select(value);
```

The DECISION is FulfilmentEditor's, down to the phone it reads before dropping and the silence it returns for a no-op. See FulfilmentSwitch for why those three are one thing and why silence means not touching Message at all. All this method has left is the two things the editor cannot do for itself: speak, and buzz.

## if

```csharp
if (change.Announce is not null) Message = change.Announce;
```

Assigned ONLY on the loss. Setting Message to an empty string would still retire a pending undo (its setter calls ClearPendingUndo), so a silent switch must not touch the property at all rather than clearing it — «Отменить» armed by a removal has to survive a tap on the fulfilment button. Hence `is not null` and not `!= string.Empty`.

## Message

```csharp
Message = OrderPromise.Describe(promised, timeProvider.GetLocalNow(), timeProvider.LocalTimeZone);
```

Overdue is stated, because it is not obvious from the figure. «Заказ к 14:20» at 15:05 is a different order from «Заказ к 14:20» at 13:00 and the sentence has to say which one this is — otherwise the operator reads their own confirmation as a mistake. Describing the LOCAL `promised` rather than re-reading the property: the sheet was opened on the order's current state, so a dismissal is a null and this is the only write. Reading it back would be a second source of truth for a value that is already in hand.

## MenuViewModel

```csharp
public MenuViewModel(
```

The autosave gate and the pending cancellation moved to DraftAutosave, with the reason for the gate: two autosaves must never write the active-cart row concurrently. They run on separate DbContexts, and the loser got DbUpdateConcurrencyException ("expected 1 row, affected 0"), which meant the draft was silently not persisted at all and a killed app lost the cart.

## menu

```csharp
menu = new MenuCatalogue(catalog, combos, timeProvider);
```

The three Collaborators are built HERE, before anything reads them, and that position is load-bearing twice over. The Cart subscription below reads a proxy onto cart, and the command lambdas further down close over all three. The compiler caught the second case as CS8602 - which is the one warning worth having here, because it is not a style complaint but a genuine "this could be null at run time". They cannot be field initialisers either, because they read the constructor's parameters - a field initialiser cannot see those. See MenuCatalogue for the clock.

## composition

```csharp
composition = new CompositionResolver(
```

The resolver speaks through the shell's two channels rather than owning them: `announce` is `Message`, whose setter retires a pending undo, and `sayError` is SetError. Both are written as the assignment they stand for, so the resolver cannot accidentally retire an undo on a path that did not mean to - `Message = string.Empty` clears it, and that has to stay true.

## cart

```csharp
cart.Changed += () =>
```

cart.Changed, NOT Cart.CollectionChanged. The collection fires when a line ARRIVES or LEAVES and stays silent when the operator presses «+» on one that is already there - StepUp writes the quantity and nothing else. Listening to the collection therefore missed the common case entirely: a quantity change was never autosaved, and a till killed after one came back with a draft behind what had been built. Found by testing this extraction on the device, and it predates the extraction - the wiring at HEAD was identical.

## cart

```csharp
cart.PropertyChanged += (_, e) =>
```

The cart owns the total now, but the button that QUOTES it is the shell's, so the shell has to hear about the total moving. Without this the checkout button goes on quoting the amount the cart had before the change — a button promising a price the checkout does not charge, and exactly the defect the old Total setter existed to prevent.

## remarks

```csharp
/// <remarks>Почему так — `docs/decisions/menu.md`</remarks>
```

The menu board: dishes, bundle tiles and the category strip, with the filter over all three. The second Collaborator, and the one with the smallest dependency list the plan predicted.

## checkoutCoordinator

```csharp
private readonly CheckoutCoordinator checkoutCoordinator;
```

The sale: pre-flight the stock, take the money, book the order. Returns what happened; the
screen is cleared here, by the shell, because "an order was booked" and "the till is empty" are
not the same fact. The sixth Collaborator.

## parkedCarts

```csharp
private readonly ParkedCarts parkedCarts;
```

Parking a receipt under a name, and picking one back up. Not a sale - no money, no stock check,
no order - which is why it is a separate class from the sale despite sharing a file with it
until now.

## PageHorizontalPadding

```csharp
private const double PageHorizontalPadding = 32;      // Grid Padding="16", both sides
```

── Product grid width contract ────────────────────────────────────────────────────────────────── The two-column grid clipped on narrow windows, and the fix had to be found empirically because .NET MAUI 10 offers no way to state a column width: * ItemsLayout.ItemWidth / ItemHeight were REMOVED from ItemsLayout in 10.0.101 (only Orientation, SnapPointsAlignment and SnapPointsType are left), so the grid cannot be told how wide a cell is; * WidthRequest AND MaxWidthRequest on the card are both ignored for sizing — the platform's ItemsWrapGrid sets the container width itself. A literal WidthRequest="150" still measured 190px columns; MaxWidthRequest="150" shrank the card's content without moving the column at all. What the grid still honours is Span, so the contract is expressed as the span: two columns while each one is still wide enough to be useful, one below that. A single full-width column cannot be clipped, and a café tablet still gets the two-column board. This also removes the latch. ItemsWrapGrid keeps whatever column width it settled on, so a window narrowed after startup kept the wide window's columns and the pair overran the content box — measured: at a 340px window the first column stayed at 170px and the pair overran by 7px. Changing the span re-lays the grid out, so the width is derived from the current page width every time instead of from history. The four constants mirror Views/MenuPage.xaml and must be changed with it, and are in device independent pixels — Window.Width is DIPs, which on this machine's 125% display is 0.8x the physical window width (measured: a 460px window reports 368).

## public

```csharp
public int ProductColumnSpan
```

Product cards per row: two while each column would still be at least
`MinimumProductCardWidth` wide, one below that. Applied by the page to the
named `GridItemsLayout`, which is the only width-related member the grid still has.

## ClearPendingUndo

```csharp
ClearPendingUndo();
```

…and it retires the pending undo. One message owns the strip, so an «Отменить» left sitting beside "Номер сохранён." would offer to put back a line whose removal the operator has long since moved past. A removal therefore arms its undo AFTER writing its own message — see RemoveItem.

## remarks

```csharp
/// <remarks>`docs/decisions/menu.md`</remarks>
```

── Undo of a removal ─────────────────────────────────────────────────────────────────────── Baymard's five requirements for a cart include "provide an undo option if a cart item is removed", and NN/g found users "accidentally added the same item to their cart multiple times" — both point the same way: removing is the mistake worth making recoverable, adding is not, because a second add is obvious on the row itself. NO TIMER, and that is the design rather than an omission. The undo lives in the message strip that was already there, so it costs no additional row: the strip is present whenever a message is, and absent otherwise. It is retired by the NEXT message rather than by a clock (Message's setter calls ClearPendingUndo), so «Отменить» is offered for exactly the outcome it belongs to and can never sit beside an unrelated one. Nothing has to be torn down on a timer, so nothing leaks if the page is left. The removed line is put back at the INDEX it was removed from, not appended: a cart read top-to-bottom is a sequence, and a cashier who removes the third line and undoes it expects the third line back.

## public

```csharp
public bool IsErrorMessage
```

Whether `Message` reports a failure. One string carried both outcomes
and the label was styled SecondaryLabel either way, so the confirmation of a created
order and a failed add looked alike — and the confirmation sat last on the screen,
under the button, in the flow where it matters most. Set via `SetError`.

## SelectedFilterKey

```csharp
public Guid SelectedFilterKey => menu.SelectedFilterKey;
```

── Which chip is selected ────────────────────────────────────────────────────────────────── A KEY, not a Category, and it belongs to the catalogue now. The strip holds three kinds of chip — «Все», the real categories and «Комбо» — and two of them have no Category at all. The old test was chip.Category?.Id == SelectedCategory?.Id || (chip.IsAll && SelectedCategory is null), which reads correctly for a strip of one kind of chip and is WRONG for this one: when no category is selected both sides are null, so every category-less chip — «Все» AND «Комбо» — compared equal and lit up together. Comparing the chip's own identity removes the whole class of bug rather than adding a case to it. See MenuFilter.

## IsCombosOnly

```csharp
public bool IsCombosOnly => menu.IsBundlesOnly;
```

True while the «Комбо» chip is selected. The product grid is then hidden, because a bundle
is not a `Product` and no amount of filtering `FilteredProducts` can produce
one — «filter to combos only» means the grid is empty, not narrowed.

## remarks

```csharp
/// <remarks>`docs/decisions/menu.md`</remarks>
```

─ Fulfilment, contact and time ──────────────────────────────────────────────────────────────────────────────────── Three facts about the ORDER rather than about any line, all of them now FulfilmentEditor's. EVERY MEMBER IN THIS SECTION IS GET-ONLY, AND THAT IS THE POINT. The first version of these proxies kept their own backing fields and setters, so a write landed in the shell's field while the getter read the editor's - two states, and the write invisible to every read. The_cart_starts_as_counter_service_and_the_toggle_moves_it caught it. Removing the write surface altogether makes that class of bug unrepresentable rather than merely fixed. Writes go through the editor's named operations - Select, SetPhone, SetPromise, SetExpanded, Reset. See the editor for why those are named rather than properties.

## PayAndCreateCommand

```csharp
public IAsyncRelayCommand PayAndCreateCommand { get; }
```

Takes the payment and books the order paid — or books it to be paid on collection, which the
operator chooses inside the sheet. «Оплата при выдаче» left this screen and became a second
exit from the payment sheet, so `CreateWithoutPaymentCommand` went with the button that
used to carry it and this command now owns both outcomes.
