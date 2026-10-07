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
