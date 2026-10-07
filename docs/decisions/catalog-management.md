# Управление каталогом

Решения, вынесенные из комментариев кода в docs/decisions/ 2026-10-07, при выполнении
пункта «комментарии в docs/decisions/» из docs/PLAN-architecture-debt.md.

Текст перенесён дословно; переведена только разметка XML в markdown. Смысл и формулировки
не трогались. В коде на этом месте осталась строка-указатель на этот документ.

## CatalogManagementViewModel

```csharp
public partial class CatalogManagementViewModel : ObservableObject
```

What used to be here and is not, by the owner's decision: the product SearchBar, the
«Выбрать» selection mode and the bulk percentage-price adjustment it fed, and the CSV
export/import. The last of those moved to `SettingsViewModel` — see
`SettingsViewModel.Catalog.cs` for why, and for the confirmation the import gained on the
way. Nothing else moved with them, and this ViewModel no longer takes an `IFileService`.

## SearchDebounce

```csharp
private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);
```

This timer used to serve two things — the product SearchBar and the category chips — and the
SearchBar is gone from this page by the owner's decision. The timer therefore has one caller
left, `SelectedProductCategory`, and it is still worth having there, which is
what the previous note got wrong when it described the debounce as having gone with the
search box. The chip is a single discrete tap with nothing to debounce on its own account;
what it needs the delay for is `RebuildCategoryFilters`, which reassigns
`SelectedProductCategory` on every load. Without the delay each load scheduled a
filter pass that raced the load that scheduled it.
`ProductSearchText` is the second caller and it stays too. Nothing binds it any
more — that is the whole point of the search's removal — but the in-memory filter and its
debounce are the property's behaviour, not the control's, so they were left intact rather
than made conditional on a SearchBar that is not coming back.

## Sections

```csharp
public ObservableCollection<CatalogSection> Sections { get; } =
```

«Комбо» sits immediately after «Товары», and the order is not cosmetic: a bundle's price is
the sum of dishes, so the section a manager needs while editing one is the one holding those
dishes. Putting it last would put «Модификаторы» and «Ингредиенты» between the two.

## AddButtonText

```csharp
public string AddButtonText => section switch
```

The button adds whatever entity is open, so a static "Добавить" would be wrong on four
of the five sections. Material 3 asks for one or two words on an extended FAB —
"Добавить группу модификаторов" was four and pushed the pill across a third of a phone
screen, so the modifiers section names the thing an operator thinks of instead of the
record type.

## Plural

```csharp
private static string Plural(int count, string one, string few, string many) =>
```

A one-line delegate to `TextFormat.Plural`. The rule used to live here as a
private copy, which meant a second screen needing it had to either duplicate the rule — and a
duplicated rule gets fixed in one place only — or take a dependency on a ViewModel. The word
choice is a formatting concern and now sits with the rest of them in Core.

## remarks

```csharp
/// <remarks>Почему так — `docs/decisions/catalog-management.md`</remarks>
```

Catalogue management list: sections, category filtering, availability toggles and the row overflow menus. Create/edit forms live in ViewModels/Catalog/* and open through ; the page shows modally and reloads on save.

## Combos

```csharp
public ObservableCollection<ComboRowViewModel> Combos { get; } = new();
```

Bundles, already wrapped for the list because their price is a sum and not a field — see
`ComboRowViewModel`. The rows are rebuilt on every load, which is how a change to
a dish's price reaches this screen: the sum is recomputed from the freshly read components.

## PageHorizontalPadding

```csharp
private const double PageHorizontalPadding = 24;      // content Grid Padding="12" both sides
```

─── Responsive width contract ────────────────────────────────────────────────────────────────── Same pattern as MenuViewModel.ProductColumnSpan: the window width is pushed in from the page (a Shell-hosted ContentPage never gets OnSizeAllocated), and the product grid's column span is derived from it. ItemsLayout is a BindableObject outside the visual tree, so {Binding} on Span does not reliably inherit the page's BindingContext — the span is assigned from code. The constants mirror Views/CatalogManagementPage.xaml and must be changed with it.

## public

```csharp
public int ProductColumnSpan
```

Product cards per row: derived from a target minimum card width, not raw breakpoints.
The column count is floor(contentWidth / (minCardWidth + spacing)), clamped to 1–3.
Applied by the page to the named `GridItemsLayout`, which is the only width-related
member the grid still has (ItemWidth was removed from ItemsLayout in MAUI 10).

## NoticeLevel

```csharp
public enum NoticeLevel { None, Success, Error }
```

The severity of . The one notice label carries both successes ("Скопировано: …") and errors, so the colour and the leading icon are driven by this flag — colour alone would not tell them apart. Named NoticeLevel, not NoticeSeverity: a nested enum may not share its name with a property of the declaring class (CS0102). The *property* keeps the name NoticeSeverity because that is what the page binds.

## LoadAsync

```csharp
public async Task LoadAsync()
```

Loads the whole catalogue. Re-entrancy: the command is constructed without
`AsyncRelayCommandOptions.AllowConcurrentExecutions` so it cannot start a
second run, and the method no longer bails on `IsBusy`. The old bare
`if (IsBusy) return;` was the pull-to-refresh trap: RefreshView sets IsRefreshing
true, the TwoWay binding pushed that into IsBusy, and the guard then returned without
ever clearing the flag — the spinner spun forever (dotnet/maui#12469).
Callers that may overlap (the ShowDeleted setter, which cannot await) start a fresh run
that re-reads ShowDeleted; the later-started run finishes last, so its value is the one
on screen.

## SyncWith

```csharp
Combos.SyncWith(
```

Bundles, LIVE ONLY — the «Показывать удалённые» switch above belongs to the products section and is not consulted here. There is no restore for a soft-deleted bundle (IComboService has DeleteComboAsync and nothing that brings one back), so listing a deleted one would put a row on screen with no way to act on it and no way to undo the deletion. Not listing it is the honest state; the bundle stays in history either way, because a sold composition is a snapshot.

## RebuildCategoryFilters

```csharp
private void RebuildCategoryFilters()
```

Rebuilds the filter chip strip. Chip instances are recreated on every load (the same
pattern the other lists use), so the selected state is re-applied from
`SelectedProductCategory` rather than carried across.

## ScheduleFilterRefresh

```csharp
private void ScheduleFilterRefresh()
```

Restarts the debounce timer on every filter change, so the filter runs once the operator
stops acting. It used to fire per keystroke, which re-scanned the whole catalogue and
re-diffed the list on each character and stutters on a long catalogue.
