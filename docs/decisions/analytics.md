# Аналитика смены

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## ProductAnalyticsLineRow

```csharp
public sealed partial class ProductAnalyticsLineRow : ObservableObject
```

An `ObservableObject` rather than the Core record, and the ONLY reason is the bindable
`IsVisible`: the projection already dropped the lines a filter hid, so there is no
per-line visibility logic here — the row is asked to disappear or not by the ViewModel when it
rebuilds, never by a row deciding something about itself.

## ProductAnalyticsDishRow

```csharp
public sealed partial class ProductAnalyticsDishRow : ObservableObject
```

The totals are of what the header CONTAINS, which the projection has already filtered: a dish
whose rows a search term removed reports only what is left. That is a deliberate difference from
the shift report, where the figure is the dish's whole shift — on a filtered screen the number
the reader adds up from the rows below must be the number on the header.

## ProductAnalyticsSectionChip

```csharp
public sealed partial class ProductAnalyticsSectionChip : ObservableObject
```

The count is on the chip because deciding where to look is an arithmetic question, and it is
text rather than a badge so a screen reader reads it with the name — the same reasoning as
`OrderFilterChip`, which this mirrors.

## public

```csharp
public ShiftChoice? SelectedShift
```

The setter is where the analysis used to be missing, and it was not a small gap: picking
another shift in the picker changed the label at the top and NOTHING else. The cards and the
breakdown kept the previous shift's figures, so the page said one shift's name over another
shift's revenue — verified on the emulator, where «Завершённая · 30.09» sat on top of a
«Продано позиций 30» that belonged to a different shift entirely, and «Обновить аналитику»
existed only to paper over it. The whole page is ABOUT one shift, so its selection is an
input to the page, not a display preference.
Guarded against self-invocation: `LoadAsync` assigns this while restoring the
previous selection, and the analysis it then runs must not start a second one.

## CardRefundsText

```csharp
public string CardRefundsText => refundsCard <= 0
```

Core names one net figure — `ExpectedCashNow` — and no net card figure, because card
money is not physically countable and there is nothing to count it against. The net is
derived here, in one place, and the caption says which part of it came back, so the cell
cannot be read as a gross "how much we took" and quietly disagree with the shift report's
«Принято картой». It is hidden at zero: a caption with nothing to add would make this one
card taller than the eight around it for no information.

## public

```csharp
public CashReconciliation? Reconciliation
```

A computed property would have been shorter, but the notifications it drives are the point:
every line of the card is derived from the same record, and a card that shows the frozen
expectation next to a difference computed from a previous selection is worse than no card.

## DifferenceColor

```csharp
public Color DifferenceColor => HasDiscrepancy
```

The tint used to arrive as a `DataTrigger` Setter holding a bare
`{StaticResource Danger}`. A trigger's Setter takes a VALUE, not a binding expression,
so the token was resolved once at parse time and stayed on the light palette in every theme:
Danger `#D32F2F` on `BackgroundDark #121212` is 3.76:1, where DangerDark
`#FF9E8E` on the same surface is 9.40:1. Resolved here instead, against the live theme.
The untinted branch has to NAME a colour rather than defer to the implicit
`Style TargetType="Label"`, because a local binding suppresses the style's own setter:
`Resolve("Black", "White")` reproduces exactly what that style declares
(`AppThemeBinding Light=Black, Dark=White`, Resources/Styles/Styles.xaml), so the
balanced case looks identical to before. If that implicit style's TextColor ever changes,
this property has to change with it — that is the cost of moving a trigger into a
ViewModel, and it is recorded here rather than left to be discovered.
Nothing here is colour-only. `DifferenceText` spells the direction out in words
and `DifferenceHint` repeats it for a screen reader, which cannot see a tint.

## CashInDrawerText

```csharp
public string CashInDrawerText => TextFormat.Money(CashInDrawer);
```

Re-raised from `NotifyStale` rather than from its own setter, because the
staleness note quotes this same figure in words — see that method.

## ShiftAnalyticsViewModel

```csharp
public partial class ShiftAnalyticsViewModel
```

Split out of `ShiftAnalyticsViewModel` because it is one concern with one rule
running through it: **nothing here filters an already-filtered list**. Every change to the
section selection, the search term, the sort or the grouping re-runs
`ProductAnalyticsProjection.Build` over `unfilteredRows` — the raw
breakdown as the query returned it. Filtering in place would mean a chip could not be unchecked,
and the second search term would be applied to the first one's results.
The projection itself is in Core and has no MAUI in it; this file is the adapter that turns its
records into notifying rows the markup can bind.

## foldedDishes

```csharp
private readonly HashSet<string> foldedDishes = new(StringComparer.CurrentCultureIgnoreCase);
```

FOLDED, not opened. The default is OPEN, so the set must record the exceptions — collecting
the opened dishes would start from an empty set and fold everything on the first render, which
is precisely what this did before the name was corrected.
A FIELD rather than a read of the collections at rebuild time, because
`ObservableCollectionSync.SyncWith{T, TKey}` replaces an item whose key it already
holds and the replacement carries no fold state of its own.

## public

```csharp
public string FilterPanelSummary
```

Stated rather than implied because the panel is CLOSED by default. A trigger that only reads
«Фильтры и сортировка» leaves the reader unable to tell an unfiltered list from a filtered
one, and on this screen those two can look identical — the cards above never change.

## ApplyAnalytics

```csharp
private void ApplyAnalytics(IReadOnlyList<ProductAnalyticsRowData> rows)
```

Synchronous on purpose: it does no I/O, it only re-projects rows already in hand, and there is
nothing to await. A filter tap calls it directly rather than going through a command, so the
list re-projects within the same frame as the touch.

## RebuildSectionChips

```csharp
private void RebuildSectionChips()
```

Off the unfiltered rows on purpose. A chip strip built from what survives the filter would
make every other section vanish the moment one is picked — so the user could narrow once and
have no way back. The counts on the chips are the shift's totals for that section, not what
is currently shown, because they answer "how much is in there" rather than "how much am I
looking at".
«Без раздела» is a chip like any other, not an absence: a dish in no section is real revenue
and the manager has to be able to isolate it as easily as any named section.

## RebuildProductRows

```csharp
private void RebuildProductRows()
```

Expanded state is carried ACROSS rebuilds by dish name, which is why
`ProductAnalyticsDishRow` keeps its `ProductAnalyticsDishRow.IsExpanded`
when the projection hands back a new instance of the same dish: a filter change or a sort
flip must not fold a dish the reader had opened. It is dropped on a shift change instead,
where the whole set of dishes is different — see `CollapseAllDishes`.

## ToggleDish

```csharp
private void ToggleDish(ProductAnalyticsDishRow? dish)
```

The set is updated HERE, at the tap, and not read back off the rows during a rebuild. A fold
has to survive the next filter change or sort flip, and a rebuild cannot tell a dish the
reader never touched from one they had opened.

## RowKey

```csharp
private static string RowKey(ProductAnalyticsLineRow row) => $"{row.ProductName}|{row.ModifierName}";
```

Both halves, because a dish sold plain and with a modifier is two rows with the same dish name
— keying on the dish alone would collapse them into one, silently dropping a sale from the
list. This is the same pair the query groups by.

## ShiftRevenueShare

```csharp
private double ShiftRevenueShare(decimal value) =>
```

The denominator is the WHOLE shift, never the visible subset. A share that moves when the
filter moves is not a share of anything the manager can hold in their head, and a chip that
said «34% выручки» on one tap and «91%» on the next would be worse than saying nothing.

## IOrderReporting и IShiftLedger

```csharp
private readonly IOrderReporting reporting;
private readonly IShiftLedger ledger;
```

Two ports, not the old flat `IOrderService`. This screen read aggregates and a list of shifts, and held an interface that also closed the till — a report reconciling a drawer it only ever displays. The shift list comes from `IShiftLedger` because that IS a shift question; the numbers come from `IOrderReporting`, which is where their definitions live. Named `ledger` and `reporting` rather than `shifts`/`reports` because both methods below declare locals of those names, and a field a local silently shadows is a bug waiting for someone to add one line to the wrong scope.

