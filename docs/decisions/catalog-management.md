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

