using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace CafePos.Presentation.ViewModels;

/// <summary>The product breakdown's filter, sorting and grouping — the half of the analytics screen that lives behind the «Фильтры и сортировка» panel.</summary>
/// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

public partial class ShiftAnalyticsViewModel
{
    /// <summary>The breakdown exactly as the query returned it.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    private IReadOnlyList<ProductAnalyticsRowData> unfilteredRows = [];

    /// <summary>Which sections are selected. EMPTY means "no section filter" — see the type.</summary>
    private readonly HashSet<Guid?> selectedSections = [];

    private ProductAnalyticsSortCriterion sortCriterion = ProductAnalyticsSortCriterion.Quantity;
    private ProductAnalyticsSortDirection sortDirection = ProductAnalyticsSortDirection.Descending;
    private ProductAnalyticsGrouping grouping = ProductAnalyticsGrouping.SectionsAndDishes;

    /// <summary>The shift's whole revenue, the denominator every share on this screen is taken against.</summary>
    private decimal shiftRevenue;

    /// <summary>The dish names the reader has FOLDED, carried across a rebuild.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private readonly HashSet<string> foldedDishes = new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Stands in for «Без раздела» as a dictionary key. A null key is not merely a lookup miss — throws — so the unfiled section needs a real id.</summary>

    private static readonly Guid UnfiledSectionKey = Guid.Empty;

    /// <summary>Populates the three control strips. Called from the constructor because a control strip that only appears after the first load is a strip that flashes empty on every open.</summary>

    private void SeedControls()
    {
        SortOptions.Add(new ProductAnalyticsSortOption(ProductAnalyticsSortCriterion.Quantity, "по количеству"));
        SortOptions.Add(new ProductAnalyticsSortOption(ProductAnalyticsSortCriterion.Revenue, "по выручке"));
        SortOptions.Add(new ProductAnalyticsSortOption(ProductAnalyticsSortCriterion.Name, "по названию"));

        GroupingOptions.Add(new ProductAnalyticsGroupingOption(ProductAnalyticsGrouping.SectionsAndDishes, "по разделам и блюдам"));
        GroupingOptions.Add(new ProductAnalyticsGroupingOption(ProductAnalyticsGrouping.Dishes, "по блюдам"));
        GroupingOptions.Add(new ProductAnalyticsGroupingOption(ProductAnalyticsGrouping.None, "без группировки"));

        foreach (var option in SortOptions) option.IsSelected = option.Criterion == sortCriterion;
        foreach (var option in GroupingOptions) option.IsSelected = option.Grouping == grouping;
    }

    // ── The panel trigger ─────────────────────────────────────────────────────────────────────

    /// <summary>What the collapsed panel says it is doing, in words: which sections, whether a search is active, and how the list is ordered.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    public string FilterPanelSummary
    {
        get
        {
            var sections = selectedSections.Count switch
            {
                0 => "все разделы",
                1 => SectionName(selectedSections.First()),
                _ => $"{selectedSections.Count} раздела"
            };
            var search = HasSearch ? $" · поиск «{SearchText.Trim()}»" : string.Empty;
            var sort = $" · {SelectedSortName} {SortDirectionName}";
            return $"{sections}{search}{sort} · {SelectedGroupingName}";
        }
    }

    /// <summary>The same sentence with the panel's state spelled out, for a screen reader.</summary>
    public string FilterPanelHint =>
        $"Фильтры и сортировка. {FilterPanelSummary}. {(IsFilterPanelOpen ? "Панель открыта" : "Панель закрыта")}";

    public string SelectedSortName => sortCriterion switch
    {
        ProductAnalyticsSortCriterion.Quantity => "по количеству",
        ProductAnalyticsSortCriterion.Revenue => "по выручке",
        _ => "по названию"
    };

    public string SelectedGroupingName => grouping switch
    {
        ProductAnalyticsGrouping.None => "без группировки",
        ProductAnalyticsGrouping.Dishes => "по блюдам",
        _ => "по разделам и блюдам"
    };

    /// <summary>«по убыванию» / «по возрастанию». A word, never an arrow alone.</summary>
    public string SortDirectionName => sortDirection == ProductAnalyticsSortDirection.Descending
        ? "по убыванию"
        : "по возрастанию";

    public string SortDirectionGlyph => sortDirection == ProductAnalyticsSortDirection.Descending ? "↓" : "↑";

    /// <summary>What the direction control says when tapped, so the result is predictable.</summary>
    public string SortDirectionHint =>
        $"Сортировка: {SelectedSortName}, {SortDirectionName}. Нажатие — {SortDirectionButtonHint}";

    private string SortDirectionButtonHint => sortDirection == ProductAnalyticsSortDirection.Descending
        ? "по возрастанию"
        : "по убыванию";

    /// <summary>── Grouping visibility ──────────────────────────────────────────────────────────────────── Exactly one of the three list shapes is bound at a time.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>


    public bool IsSectionGrouping => grouping == ProductAnalyticsGrouping.SectionsAndDishes;
    public bool IsDishGrouping => grouping == ProductAnalyticsGrouping.Dishes;
    public bool IsFlatGrouping => grouping == ProductAnalyticsGrouping.None;

    /// <summary>    /// <summary>The section's own heading, which is no longer the constant «Продажи по блюдам и модификаторам»that was always wrong the moment a head...</summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>that was always wrong the moment a header went above a modifier r...</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    public string ProductSectionTitle => grouping switch
    {
        ProductAnalyticsGrouping.SectionsAndDishes => "Продажи по разделам и блюдам",
        ProductAnalyticsGrouping.Dishes => "Продажи по блюдам и модификаторам",
        _ => "Продажи по позициям"
    };

    // ── The filter summary under the cards ─────────────────────────────────────────────────────

    private int visibleQuantity;
    private decimal visibleRevenue;
    private int totalQuantity;

    /// <summary>«Показано 12 позиций из 137 на сумму 3 200,00 ₽»</summary>
    public string VisibleSummaryText
    {
        get
        {
            if (totalQuantity == 0) return string.Empty;
            var shown = $"Показано {visibleQuantity} {Plural(visibleQuantity, "позиция", "позиции", "позиций")}" +
                $" из {totalQuantity} {Plural(totalQuantity, "позиции", "позиций", "позиций")}" +
                $" на сумму {TextFormat.Money(visibleRevenue)}";
            // «1 позиция из 21 позиции» — the total is read as a count with its own plural form, not
            // as a continuation of the first. Getting this wrong («из 21 позиций») is the single most
            // common Russian plural bug and it is exactly what the shared helper exists to prevent.
            return grouping == ProductAnalyticsGrouping.None
                ? shown
                : $"{shown} · {SectionTotalCaption}";
        }
    }

    private string SectionTotalCaption
    {
        get
        {
            if (!IsSectionGrouping) return string.Empty;
            var count = SectionRows.Count;
            return $"{count} {Plural(count, "раздел", "раздела", "разделов")}";
        }
    }

    /// <summary>The same sentence, spelled for a screen reader.</summary>
    public string VisibleSummaryHint
    {
        get
        {
            if (totalQuantity == 0) return string.Empty;
            // Spoken form, so the numbers are heard rather than assembled: «1 позиция из 21 позиции».
            var text = $"Показано {visibleQuantity} {Plural(visibleQuantity, "позиция", "позиции", "позиций")} " +
                $"из {totalQuantity} {Plural(totalQuantity, "позиции", "позиций", "позиций")} " +
                $"на сумму {TextFormat.Money(visibleRevenue)}";
            return SectionTotalCaption.Length > 0 ? $"{text}. {SectionTotalCaption}" : text;
        }
    }

    /// <summary>Hidden when the list is empty: a summary of nothing is noise above the empty state.</summary>
    public bool HasVisibleSummary => totalQuantity > 0 && visibleQuantity > 0;

    // ── Loading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Takes a fresh breakdown from the query and rebuilds the list, the chips and the summary.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private void ApplyAnalytics(IReadOnlyList<ProductAnalyticsRowData> rows)
    {
        unfilteredRows = rows;
        shiftRevenue = rows.Sum(row => row.Revenue);
        totalQuantity = rows.Sum(row => row.Quantity);

        RebuildSectionChips();
        RebuildProductRows();
    }

    /// <summary>Rebuilds the section chips from the UNFILTERED rows.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private void RebuildSectionChips()
    {
        /// <summary>The section each line belongs to, straight off the query.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        var bySection = new Dictionary<Guid, (string Name, int Quantity, decimal Revenue)>();
        foreach (var row in unfilteredRows)
        {
            var key = row.CategoryId ?? UnfiledSectionKey;
            var name = row.CategoryName ?? ProductAnalyticsProjection.SectionWithoutName;
            if (!bySection.TryGetValue(key, out var current))
            {
                current = (name, 0, 0m);
            }

            bySection[key] = (current.Name, current.Quantity + row.Quantity, current.Revenue + row.Revenue);
        }

        /// <summary>DROPPED FIRST, and the order matters.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        var available = bySection.Keys.ToHashSet();
        selectedSections.RemoveWhere(id => !available.Contains(id ?? UnfiledSectionKey));

        var chips = bySection
            .OrderBy(pair => pair.Value.Name, StringComparer.CurrentCulture)
            // The sentinel goes back to null here, so the chip carries the same nullable id the
            // filter compares against and «Без раздела» is selectable like any other section.
            .Select(pair => new ProductAnalyticsSectionChip(
                pair.Key == UnfiledSectionKey ? null : pair.Key,
                pair.Value.Name)
            {
                Count = pair.Value.Quantity,
                Revenue = Money.Round(pair.Value.Revenue),
                IsSelected = selectedSections.Contains(pair.Key)
            })
            .ToList();

        /// <summary>Keyed on the (id, name) PAIR, not on the id.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        SectionChips.SyncWith(chips, chip => (chip.SectionId, chip.Name));
        foreach (var chip in SectionChips) chip.SetShare(ShiftRevenueShare(chip.Revenue));

        OnPropertyChanged(nameof(FilterPanelSummary));
        OnPropertyChanged(nameof(FilterPanelHint));
    }

    /// <summary>Runs the projection and syncs the three list shapes.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private void RebuildProductRows()
    {
        var filter = new ProductAnalyticsFilter(selectedSections.ToHashSet(), SearchText);
        var sort = new ProductAnalyticsSort(sortCriterion, sortDirection);
        var projection = ProductAnalyticsProjection.Build(unfilteredRows, filter, sort, grouping);

        visibleQuantity = projection.VisibleQuantity;
        visibleRevenue = projection.VisibleRevenue;

        var sections = projection.Sections.Select(BuildSection).ToList();
        var dishes = projection.Dishes.Select(BuildDish).ToList();

        // Forget the fold of a dish that is no longer on screen at all, so a name cannot accumulate
        // stale state for a dish this shift does not sell. One this shift DOES sell but the filter
        // hid keeps its fold — that is the whole point of carrying it across a filter.
        foreach (var name in foldedDishes.ToArray())
        {
            var onScreen = sections.Any(section => section.Dishes.Any(dish => dish.Name == name)) ||
                dishes.Any(dish => dish.Name == name);
            if (!onScreen) foldedDishes.Remove(name);
        }

        SectionRows.SyncWith(sections, row => row.Name);
        DishRows.SyncWith(dishes, row => row.Name);
        LineRows.SyncWith(projection.Lines.Select(line => new ProductAnalyticsLineRow(line)).ToList(), RowKey);

        HasNoProductRows = visibleQuantity == 0;
        IsFilteredToNothing = projection.IsFilteredToNothing;

        NotifyGroupingChanged();
        OnPropertyChanged(nameof(HasNoProductRows));
        OnPropertyChanged(nameof(IsFilteredToNothing));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(EmptyListText));
        OnPropertyChanged(nameof(VisibleSummaryText));
        OnPropertyChanged(nameof(VisibleSummaryHint));
        OnPropertyChanged(nameof(HasVisibleSummary));
    }

    /// <summary>A section row, with each dish below it restored to its remembered fold state.</summary>
    private ProductAnalyticsSectionRow BuildSection(ProductAnalyticsSection section)
    {
        var row = new ProductAnalyticsSectionRow(section, ShareText);
        foreach (var dish in row.Dishes)
        {
            if (foldedDishes.Contains(dish.Name)) dish.IsExpanded = false;
        }

        return row;
    }

    private ProductAnalyticsDishRow BuildDish(ProductAnalyticsDish dish)
    {
        var row = new ProductAnalyticsDishRow(dish, ShareText(dish.Revenue));
        if (foldedDishes.Contains(dish.Name)) row.IsExpanded = false;
        return row;
    }

    /// <summary>    /// <summary>Forgets every fold and opens every dish, called when the selected shift changes: the nextshift sells different dishes, and a manager...</summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>shift sells different dishes, and a manager who folded «Капучино» away...</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    private void OpenAllDishes()
    {
        foldedDishes.Clear();
        foreach (var dish in DishRows) dish.IsExpanded = true;
        foreach (var dish in SectionRows.SelectMany(section => section.Dishes)) dish.IsExpanded = true;
    }

    private void NotifyGroupingChanged()
    {
        OnPropertyChanged(nameof(IsSectionGrouping));
        OnPropertyChanged(nameof(IsDishGrouping));
        OnPropertyChanged(nameof(IsFlatGrouping));
        OnPropertyChanged(nameof(ProductSectionTitle));
        OnPropertyChanged(nameof(VisibleSummaryText));
        OnPropertyChanged(nameof(HasVisibleSummary));
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Folds or unfolds one dish, and remembers which way it went.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private void ToggleDish(ProductAnalyticsDishRow? dish)
    {
        if (dish is not { } row) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded) foldedDishes.Remove(row.Name);
        else foldedDishes.Add(row.Name);
    }

    private void ToggleSectionChip(ProductAnalyticsSectionChip? chip)
    {
        if (chip is not { } row) return;
        // The «Все» behaviour is expressed by REMOVING every id rather than by adding a sentinel,
        // because the filter treats an empty set as "no section filter" — one meaning, one place.
        if (selectedSections.Contains(row.SectionId)) selectedSections.Remove(row.SectionId);
        else selectedSections.Add(row.SectionId);

        foreach (var candidate in SectionChips) candidate.IsSelected = selectedSections.Contains(candidate.SectionId);

        RebuildProductRows();
        OnPropertyChanged(nameof(FilterPanelSummary));
        OnPropertyChanged(nameof(FilterPanelHint));
    }

    private void SelectSort(ProductAnalyticsSortOption? option)
    {
        if (option is not { } row) return;
        // Re-picking the criterion already in force REVERSES it. The alternative — a separate
        // direction control the user has to find — is one more control for the same decision, and
        // «sort by quantity» twice reading as a no-op would be worse than either.
        if (sortCriterion == row.Criterion)
        {
            sortDirection = sortDirection == ProductAnalyticsSortDirection.Descending
                ? ProductAnalyticsSortDirection.Ascending
                : ProductAnalyticsSortDirection.Descending;
        }
        else
        {
            sortCriterion = row.Criterion;
            // Money and names both read naturally largest-first; quantity does too. Every criterion
            // therefore starts DESCENDING, so switching criterion is a change of WHAT is ordered and
            // never silently flips the direction the reader already chose.
            sortDirection = ProductAnalyticsSortDirection.Descending;
        }

        foreach (var candidate in SortOptions) candidate.IsSelected = candidate.Criterion == sortCriterion;
        RebuildProductRows();
        OnPropertyChanged(nameof(FilterPanelSummary));
        OnPropertyChanged(nameof(FilterPanelHint));
        OnPropertyChanged(nameof(SortDirectionName));
        OnPropertyChanged(nameof(SortDirectionGlyph));
        OnPropertyChanged(nameof(SortDirectionHint));
    }

    /// <summary>Flips the direction while leaving the criterion alone.</summary>
    private void SortDirectionCommand()
    {
        sortDirection = sortDirection == ProductAnalyticsSortDirection.Descending
            ? ProductAnalyticsSortDirection.Ascending
            : ProductAnalyticsSortDirection.Descending;
        RebuildProductRows();
        OnPropertyChanged(nameof(FilterPanelSummary));
        OnPropertyChanged(nameof(FilterPanelHint));
        OnPropertyChanged(nameof(SortDirectionName));
        OnPropertyChanged(nameof(SortDirectionGlyph));
        OnPropertyChanged(nameof(SortDirectionHint));
    }

    private void SelectGrouping(ProductAnalyticsGroupingOption? option)
    {
        if (option is not { } row) return;
        grouping = row.Grouping;
        foreach (var candidate in GroupingOptions) candidate.IsSelected = candidate.Grouping == grouping;

        // A grouping change swaps one tree for another, and the dishes by name carry over: a dish the
        // reader had open stays open under the new shape rather than jumping shut.
        RebuildProductRows();
        OnPropertyChanged(nameof(FilterPanelSummary));
        OnPropertyChanged(nameof(FilterPanelHint));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private string SectionName(Guid? sectionId) =>
        SectionChips.FirstOrDefault(chip => chip.SectionId == sectionId)?.Name
        ?? ProductAnalyticsProjection.SectionWithoutName;

    /// <summary>A flat line's identity: the dish AND its modifier.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private static string RowKey(ProductAnalyticsLineRow row) => $"{row.ProductName}|{row.ModifierName}";

    /// <summary>A value's share of the shift's revenue as a 0..1 fraction.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    private double ShiftRevenueShare(decimal value) =>
        shiftRevenue <= 0 ? 0 : (double)(value / shiftRevenue);

    private string ShareText(decimal value)
    {
        var share = ShiftRevenueShare(value);
        return share <= 0 ? string.Empty : $"{Math.Round(share * 100)}% выручки смены";
    }

    /// <summary>Russian plural selection: one / few / many — the app's single implementation, in Core. Never written inline in markup (§5).</summary>

    private static string Plural(int count, string one, string few, string many) =>
        TextFormat.Plural(count, one, few, many);
}
