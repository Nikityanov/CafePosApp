# Доска заказов

Перенесено из комментариев к `CafePos.Presentation\ViewModels\OrdersViewModel.cs` 2026-10-07, при выполнении пункта «комментарии в
`docs/decisions/`» из `docs/PLAN-architecture-debt.md`.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки не
трогались.

## QueueSection

```csharp
public enum QueueSection
```

**WHY THREE SECTIONS AND NOT "PREPARING / READY".** The promise gates when an order is worked,
not which of two statuses it holds. Toast keeps a future order in "Pending Orders → Future Checks"
where it "appears as a preview ticket until the fire time is met", and DoorDash moves scheduled
orders into the active tab by pickup time — Square does the same. An order for 18:00 placed at
17:00 is not "готовится" and not "готов": it is not yet anybody's work, and putting it among the
working orders tells the kitchen to start now and then shows it as overdue before a minute has
passed.
The three are checked in one order and are mutually exclusive: a future time wins over being late
(an order promised for later cannot be late), and lateness wins over the plain working section.
Together they cover every active order, which is what lets the board sort by section as its PRIMARY
key without leaving rows ungrouped.

## OrderTab

```csharp
public enum OrderTab
```

An ENUM rather than a 0/1/2 index, and the crash log is the reason. With
`RelayCommand&lt;int&gt;` and `CommandParameter="0"` in XAML, MAUI hands the parameter
over as the STRING "0" and CommunityToolkit's RelayCommand throws
`System.ArgumentException: Parameter "parameter" (object) cannot be of type System.String` the
moment the chip is tapped — the board died on its first tab switch. An XAML attribute literal is
always a string, so an int-typed command parameter cannot be written that way at all: a command
parameter has to be bound, and the only markup that yields a real typed value is
`{x:Static}`.
AT NAMESPACE LEVEL, not nested inside `OrdersViewModel`. Nested, the type is
`CafePosApp.ViewModels.OrdersViewModel+OrderTab` and the XAML compiler cannot name a nested
type through a namespace prefix — it reports `XamlParseException: Type OrderTab not found in
xmlns clr-namespace:CafePosApp.ViewModels` and the page dies at parse time, before a single
control is drawn. Both crashes came from this single declaration; the second is the same mistake in
a different shape.

## now

```csharp
private readonly DateTimeOffset now;
```

Every derived value on this row — which section it is in, whether it is late, by how much —
is measured against ONE instant, taken once by the loader. A row that read the clock itself
would compute its section at a slightly different moment from the row above it, and two orders
promised for the same minute could land on opposite sides of a boundary; worse, the section a
row belongs to would change under the operator's finger with no reload at all.

## CancellationDetail

```csharp
public string CancellationDetail =>
```

A separate property rather than appended to `StatusText`, because the shift
report's history row shows the composed reason underneath the status while the orders board
does not need it at all: the board never lists a cancelled order, since
`GetActiveOrdersAsync` excludes both terminal statuses.
Worth having on the history row because the domain composes the whole story into this one
string — how much was refunded, whether stock came back, and which ingredients could not be
reversed. A voided sale with only "Отменен" on it tells a manager the order is gone and
nothing about where the money went.

## IsUnseenReady

```csharp
public bool IsUnseenReady => Model.IsUnseenReady(now);
```

The owner asked for "счётчик + точка непрочитанного" and picked the Toast pattern: a marker that
WAITS to be acknowledged rather than one that fades. A decaying highlight answers "something
changed a moment ago"; this answers "is there something you have not seen yet", which is the
question after a walk to the counter or a reboot.
The judgement is `Order.IsUnseenReady` against the loader's `now` — the same
instant every other figure on this card is measured at, so a row cannot show a dot for an order
that went ready between the read and the draw. It is a pure function of two stored moments, not a
maintained flag: the board rebuilds its rows on every auto-refresh tick, and a flag carried across
those rebuilds would eventually disagree with `ReadyAt`.
In practice only the «Ждут выдачи» tab shows it — a dot is a statement about a finished order
nobody has collected — but it is computed from the model rather than from the tab, so a ready
order that is also scheduled still says so wherever it appears.

## QueueSection

```csharp
public QueueSection QueueSection => IsScheduled
```

Three questions, asked in this order and never merged: a time in the future means the order is
a preview, a promise already past means it is late, and everything else is ordinary work. The
checks read `Order.IsScheduledAt` and `Order.IsOverdueAt`, which is
where "in the future" and "already past" are defined once for the whole app.

## QueueSectionName

```csharp
public string QueueSectionName => QueueSection switch
```

A string, not the enum, because the group header template binds to this exact value — the
header is the property, so it has to read as Russian prose. The three names are the plan's own:
«Срочные», «В работе», «По времени».
The two sections this replaces were the order's STATUS, which is a different question: a ready
order waiting for pickup is still work, and it is still late if its promise has passed. The
status is still on the card — see `StatusText` — it just no longer decides where the
card sits.

## public

```csharp
public bool ShowGroupHeader
```

Set once the rows are sorted preparing-first, not from the constructor — a row cannot
know whether an earlier row in the same group exists. The name itself is text in a
heading, so the two sections are told apart by their wording and never by colour alone.
This raises change notification, which a plain auto-property does not. Switching the
section filter rewrites the flag on rows that are already in the list and leaves the
collection itself structurally unchanged, so CollectionView recycles the cells instead of
re-inflating the templates. Without the notification the heading stayed on screen after
its section had been filtered to a single chip that already names it.

## IsOverdue

```csharp
public bool IsOverdue => Model.IsOverdueAt(now);
```

A DISPLAY STATE AND NOT A FAILURE: no system found rejects an order whose promised time has
simply elapsed in the queue — the kitchen is behind, which is a fact about the kitchen and not
something a refusal would fix. The board only lists active orders, so "still on the board" is the
whole of "still being made" here, and a ready order waiting for pickup that has run past its
promise is exactly as late as one still in the pan.

## OverdueColor

```csharp
public Color OverdueColor => PaletteAccess.Resolve("Danger", "DangerDark");
```

`StatusColor` keeps the card's border stroke, and it must: a card has to be able to
say both "this is cooking" and "this is late", which are separate facts, and recolouring the
border would make a late order look like it had a different STATUS. So the lateness marker is a
badge, and colour is its third signal rather than its first —
`OverdueText` names the state and `OverdueHint` says it in a sentence.

## PhoneText

```csharp
public string PhoneText =>
```

Masked here for the same reason it is masked on the cart: an orders board stands in the room and
is readable by whoever is standing in it, so it is not a staff-only surface in the way
`OrderDetailsPage` is — a screen somebody opens deliberately, one order at a time. The
unmasked number lives only there.

## StatusColor

```csharp
public Color StatusColor => Model.Status switch
```

Was four `Microsoft.Maui.Graphics.Colors.*` literals, which is a colour that can never
follow the theme: `AppThemeBinding` governs XAML-supplied values only, so a finished
`Color` from a ViewModel stays whatever it was built as. On a
`SurfaceDark #1E1E1E` card that left `SteelBlue #4682B4` (closed) at 4.06:1,
`Green #008000` (ready) at 3.25:1 and `Red #FF0000` (unpaid) at 4.17:1 — all
under the 4.5:1 a 13pt label owes, and all of them lighter on the SurfaceVariantDark fill
they also appear on. `ThemeColors` reads the palette and picks the light or dark key
for the live theme; the new dark tones measure 8.28:1, 8.36:1 and 9.63:1 there.
Two mappings are worth naming, because they are choices and not transcriptions.
`Colors.Gray` (#808080, 3.95:1 on white) became `Gray600`/`Gray400` — the
app's own secondary-text pairing from `SecondaryLabel` and `CountLabel`, which
is both more consistent and 4.61:1 in light instead of 3.95:1. And the closed order's
SteelBlue became `Info`/`InfoDark`: blue was already carrying "finished, nothing
outstanding", and it is the one semantic the four states could use without colliding with
the green/orange/grey of the other three.
In-progress takes `WarningText`/`WarningDark` rather than
`Warning`/`WarningDark`, for the same reason the payment line does: the
palette's only warm tone reached 3.11:1 on the light card, so the light branch of a
warning-coloured <em>label</em> needs a token dark enough to be text. 5.18:1 against
#FFFFFF, 4.96:1 against #FAFAFA, dark branch unchanged at 9.63:1.
Colour is never the only cue here and never was: `StatusText` names the state
and `StatusHint` is the screen-reader description, so re-tuning the tints
changes nothing about what the state is.

## PaymentGlyph

```csharp
public string PaymentGlyph => Model.PaymentState switch
```

It used to return a `Microsoft.Maui.Controls.Shapes.Geometry` parsed once here, which
forced this project to reference the whole MAUI framework for three strings — and it worked
only because `PathGeometryConverter` happened to be reachable. A string cannot drag in a
framework.
The page passes it through `PaymentStateGlyphConverter`, which parses it with the
same converter XAML uses for a literal `Path="M …"`. So the parsing still happens in the
UI layer, on the thread that draws it, from the same code the XAML uses — and the ViewModel
ends up holding what it always should have: the shape, as data.
Parsing per bind rather than once per process is the deliberate trade: three short path strings
on the rows that are on screen, in exchange for a presentation layer that has no opinion about
what a vector geometry is. The glyph is a mark, and a mark in a ViewModel is a string with a
colour and a name beside it.

## PaymentColor

```csharp
public Color PaymentColor => Model.PaymentState switch
```

Was `Microsoft.Maui.Graphics.Colors.Green/Orange/Red`. The pictogram is a 24dp mark
with no wording of its own, and `Colors.Red #FF0000` on a `SurfaceVariantDark
#2D2D2D` card was 3.44:1 — the state an operator most needs to notice was the hardest
one to notice. From the palette it is 6.84:1, 7.96:1 and 6.91:1 for paid / partial /
unpaid respectively.
The light branch is not a transcription of the dark one. Partial payment used
`Warning #ED6C02` there, because the palette had no warm tone dark enough to
serve as text: it is 3.11:1 on the light card #FFFFFF and 2.98:1 on #FAFAFA, both
under the 4.5:1 owed at 12pt bold. `WarningText #A35B00` replaces it at
5.18:1 and 4.96:1, so the partial state is finally legible in the light theme. On
the light card the full set now reads 5.13:1 paid, 5.18:1 partial, 4.98:1 unpaid;
the dark branch is untouched.
As with `StatusColor`, the wording is what carries the state:
`PaymentText` spells it out and `PaymentHint` is the description,
with `PaymentGlyph` a fourth, non-colour signal.

## CancelText

```csharp
public string CancelText => Model.PaidKopecks > 0
```

Written out per row rather than put in a resource, because the two variants differ and a
resource lookup cannot branch. It reads in the imperative so it fits the buttons it sits
between ("Подробнее" / "Готов"), which are also per-row wording.

## CanCancel

```csharp
public bool CanCancel => Model.Status is not OrderStatus.Cancelled;
```

Open AND closed orders alike, paid or not. A paid order is cancellable now: cancelling
voids the sale AND refunds the whole collected amount in the same transaction, so the cash
goes back to the customer and the order leaves the revenue — which is the only correct
outcome for a sale that should never have happened at all.
This used to carry `!Model.IsFullyPaid`, which was a UI workaround for a domain guard
that has since been removed along with the guard itself. The comment it replaced is worth
keeping in mind, because its reasoning was sound for what the domain did then: with no way
to put money back, a cancel button on a paid order was a control that could only fail, and a
failed cancel cost three steps (tap, confirm, reason) to reach "Нельзя отменить оплаченный
заказ". It was also a hole around the payment block — cancelling was one tap and paying was
three, so under queue pressure cancel was the tempting one, and the sale silently vanished
from the shift report while the cash stayed in the drawer. Cancelling a paid order is now a
working, money-moving operation, so neither cost applies.
Note that `Order.IsFullyPaid` was never the same predicate as the domain guard
it shadowed, which was `PaidKopecks &gt; 0`. A partially paid order has
`IsFullyPaid == false` and so passed the UI, then failed in the domain — the button
appeared and cancelled nothing. That inconsistency is gone with the guard.

## TotalPriceText

```csharp
public string TotalPriceText => TextFormat.Money(TotalPrice);
```

This exists because the order card used to format its total with a
`StringFormat="Итого: {0:F2} ₽"` in XAML, and that hard-codes both the symbol and the
digit count — neither of which can be bound. Formatting moves here so it can read the
selected currency; the label keeps the "Итого:" prefix so the wording stays in the view.

## RefreshMoneyText

```csharp
public void RefreshMoneyText()
```

The card also renders a payment-state sentence that quotes money
(`PaymentHint`, `PaymentLine`), so those are re-raised with it — a
card where the headline total moved to ₿ while the line under it still read ₽ would be worse
than one that had not updated at all.

## ItemLines

```csharp
public IEnumerable<OrderItemLine> ItemLines => Model.Items.Select(item => new OrderItemLine(
```

This is a LINQ iterator, not an `INotifyCollectionChanged` collection, and
BindableLayout only listens to INPC. The obvious conclusion — the lines are built
once and never move again — is wrong here, so read this before "fixing" it.
The row is RE-CREATED on each of the four collection actions BindableLayout
handles, Move included: it applies every one of them through
`NotifyCollectionChangedEventArgsExtensions.Apply`, where `Move` is a
`removeAt` + `insert` and `insert` calls `CreateItemView`. The
iterator is re-read each time, so the freshness does not depend on the row
instance surviving a change to the order. A recycling `CollectionView` would
be safe for the same reason: a new `BindingContext` is a new
`ItemsSource`, so `CreateChildren()` runs again.
Two things WOULD break it, and both are easy to reach by accident:
<list type="bullet">
<item>a row instance that outlives a change to the set of lines — that is, any board
action that alters the order's lines without raising `ItemLines` and without
running its own `ReloadAsync`. Today every such action reloads the board, but
that is a fact about the CALLERS and is written down nowhere in this property;</item>
<item>a caller that bypasses `SyncWith` for this list, since the refresh guards
— including the one in the `Move` branch — live inside `SyncWith` and
nothing else stands behind them.</item>
</list>

## OrderItemLine

```csharp
public sealed record OrderItemLine(string Name, int Quantity);
```

These were a single preformatted string, so a long name pushed "× 1" onto a second line
and it landed alone under the name — measured on the emulator at 158dp, where
"Капучино (Овсяное) × 1" wrapped and orphaned its quantity. A trailing quantity is a
column of its own, never a wrap casualty.

## PreparingOrders

```csharp
public ObservableCollection<OrderRowViewModel> PreparingOrders { get; } = [];
```

The owner chose two things here that a single filtered list could not do at once. The sort is
`CreatedAt` — arrival — because the board's job is handing orders over in the order they
came, and the promised time is already printed on the card. And a pre-order appears here as well
as on its own tab, at the bottom: a café wants to start a «К 18:00» order early, and hiding it
until the hour arrives means finding out too late. It sits below everything already being made
because nothing is owed to that customer yet.

## ReadyOrders

```csharp
public ObservableCollection<OrderRowViewModel> ReadyOrders { get; } = [];
```

Sorted by arrival rather than by the moment they became ready, which is the owner's explicit
instruction: the queue at the counter is worked in the order people paid, not the order the
kitchen happened to finish them.

## public

```csharp
public bool HasUnseenReady
```

The owner's addition, and it is the piece that makes the COUNT usable. A count says how many are
waiting; it does not say whether the operator already knows. Without this, «Ждут выдачи · 1» looks
identical whether that order finished thirty seconds ago or has been sitting there all morning —
and the operator has to open the tab to find out, which is the exact trip the count was meant to
save. This is Toast's tab dot, and it carries the same fact the row dot carries one level up: the
tab says there is something new, the row says which.
Computed from the rows rather than counted separately, so it cannot disagree with the dots drawn
on the cards. Announced by `AnnounceTabCounts` on every load, because the rows are rebuilt on
the auto-refresh tick and a row that has just been opened has to take the chip dot down with it.

## remarks

```csharp
/// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>
```

Section this order belongs to in the single-list layout. The orders board is one scrolling list, not two columns, because a kanban on a 411dp phone gave each card ~190dp and clipped "Подробнее" to "Подробн". Grouping keeps the visual split the board had while giving every card the full width.

## summary

```csharp
/// <summary>Whether the customer asked for a time that has not arrived yet — the order is a preview ticket, not work.</summary>
```

── Promise, lateness and contact ───────────────────────────────────────────────────────────── THE SORT KEY AND THE LATE MARKER ARE TWO SEPARATE MEASUREMENTS, AND THEY ARE NOT MERGED. Where a card SITS answers "what should be worked on, in what order" — measured by PromisedAt, then CreatedAt. The warning marker answers "has this one passed the moment it was promised" — measured by now against PromisedAt alone, with no reference to where the card is. In a real KDS those are different timers for different reasons: the warning colour on a kitchen screen measures how long an ITEM has been cooking, which is not how late the order's promise is. Folding one into the other would mean a late order could be reordered into a calm stretch of the queue and lose the fact that it is late — which is exactly the failure this layout exists to prevent. So the marker RIDES ALONG on the card and never moves it. The «Срочные» section is where late orders live, and that is a statement about where they are — not a promotion rule applied to the other two sections.

## PromisedAtText

```csharp
public string PromisedAtText => Model.PromisedAt.ToLocalTime().ToString("HH:mm");
```

When this order is due. The EXACT promised time, which a staff-facing screen may show: the range
quoted to the customer on the cart is an estimate for a time the till picks, and this is the
figure the order was actually promised and is judged against.

## PaymentState

```csharp
public PaymentState PaymentState => Model.PaymentState;
```

── Payment state ────────────────────────────────────────────────────────────────────────── Derived from the model the same way StatusColor is: a computed property on the row, not a converter. The pictogram binds PaymentGlyph (the mark) and PaymentColor (its fill); the visible line and the accessible description carry the wording, so the state is never told by colour alone.

## remarks

```csharp
/// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>
```

What the cancel button says on this card. A paid order is not just being closed off — money is going back to the customer, and the label is the only place that can say so before the operator taps.

## LoadCommand

```csharp
LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
```

AllowConcurrentExecutions: RefreshView.IsRefreshing is bound to IsBusy, so setting IsBusy = true re-triggers LoadCommand. Without AllowConcurrentExecutions the command would reject the re-entrant Execute call and the RefreshView spinner could get stuck. LoadAsync also has an `if (IsBusy) return;` guard to prevent the feedback loop from re-executing the Orders query on every cycle (infinite loading).

## PreparingTabText

```csharp
public string PreparingTabText => $"Готовятся · {PreparingOrders.Count}";
```

Tab captions with their counts. The count is on the tab rather than inside it because the reason
to look is arithmetic — is anything waiting — and reading that off a tab bar costs nothing,
while discovering it by opening the tab costs the operator their place in the queue.

## selectedTab

```csharp
private OrderTab selectedTab = OrderTab.Preparing;
```

Which tab is showing. `OrderTab.Preparing` is the default, and that is the owner's
instruction: for a single operator the queue is worked from what is being made, and the count on
«Ждут выдачи» says whether anything has finished without anyone having to look into the tab.
