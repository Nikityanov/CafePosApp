using CafePos.Core.Common;

namespace CafePos.Core.Services;

/// <summary>What the analytics list is ordered by.</summary>
public enum ProductAnalyticsSortCriterion
{
    /// <summary>Units sold. The default, and the order the shift report's breakdown has always had.</summary>
    Quantity,

    /// <summary>Money taken, which is not the same question as "how often was it sold".</summary>
    Revenue,

    /// <summary>Dish name, in the current culture.</summary>
    Name
}

/// <summary>How deep the analytics list nests.</summary>
public enum ProductAnalyticsGrouping
{
    /// <summary>One row per dish × modifier, no headers. The flat list this screen always had.</summary>
    None,

    /// <summary>A dish header with its modifier rows under it.</summary>
    Dishes,

    /// <summary>A section header, then the dish headers under it. The default.</summary>
    SectionsAndDishes
}

/// <summary>Which direction the chosen criterion runs in.</summary>
public enum ProductAnalyticsSortDirection
{
    Descending,
    Ascending
}

/// <summary>The complete ordering of the analytics list.</summary>
public sealed record ProductAnalyticsSort(ProductAnalyticsSortCriterion Criterion, ProductAnalyticsSortDirection Direction)
{
    public static readonly ProductAnalyticsSort ByQuantityDescending =
        new(ProductAnalyticsSortCriterion.Quantity, ProductAnalyticsSortDirection.Descending);

    /// <summary>The same criterion run the other way.</summary>
    public ProductAnalyticsSort Reversed() => this with
    {
        Direction = Direction == ProductAnalyticsSortDirection.Descending
            ? ProductAnalyticsSortDirection.Ascending
            : ProductAnalyticsSortDirection.Descending
    };
}

/// <summary>Which sections the list shows. An EMPTY set means every section — a selection the user has cleared, not a state the projection has to be told about separately.</summary>

public sealed record ProductAnalyticsFilter(IReadOnlySet<Guid?> Sections, string? Search)
{
    public static readonly ProductAnalyticsFilter Empty = new(new HashSet<Guid?>(), null);

    /// <summary>True when neither a section nor a search term narrows anything.</summary>
    public bool IsEmpty => Sections.Count == 0 && string.IsNullOrWhiteSpace(Search);

    /// <summary>True when something IS selected but nothing matched. A distinct fact from "the shift sold nothing", and the empty state has to say one or the other.</summary>

    public bool IsActive => !IsEmpty;
}

/// <summary>One row: a dish as sold, with one modifier or variant.</summary>
public sealed record ProductAnalyticsLine(string DishName, string ModifierName, int Quantity, decimal Revenue);

/// <summary>One dish: its name, its totals, and the rows it is made of.</summary>
public sealed record ProductAnalyticsDish(
    string Name,
    int Quantity,
    decimal Revenue,
    IReadOnlyList<ProductAnalyticsLine> Lines);

/// <summary>One section, with the dishes sold in it.</summary>
public sealed record ProductAnalyticsSection(string Name, int Quantity, decimal Revenue, IReadOnlyList<ProductAnalyticsDish> Dishes);

/// <summary>Everything the list needs in one shape, for whichever grouping is active — see `ProductAnalyticsGrouping`.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed record ProductAnalyticsProjectionResult(
    IReadOnlyList<ProductAnalyticsSection> Sections,
    IReadOnlyList<ProductAnalyticsDish> Dishes,
    IReadOnlyList<ProductAnalyticsLine> Lines,
    IReadOnlyList<string> SectionNames,
    int VisibleQuantity,
    decimal VisibleRevenue,
    int TotalQuantity)
{
    public static readonly ProductAnalyticsProjectionResult Empty = new(
        [], [], [], [], 0, 0m, 0);

    /// <summary>Nothing survived the filter, but the shift itself had rows.</summary>
    public bool IsFilteredToNothing => TotalQuantity > 0 && VisibleQuantity == 0;
}

/// <summary>Turns the flat breakdown of one shift into the filtered, sorted and grouped list the analytics screen renders.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public static class ProductAnalyticsProjection
{
    /// <summary>Builds the list. is expected unfiltered and unordered — this is the only place the order is decided.</summary>

    public static ProductAnalyticsProjectionResult Build(
        IReadOnlyList<ProductAnalyticsRowData> rows,
        ProductAnalyticsFilter filter,
        ProductAnalyticsSort sort,
        ProductAnalyticsGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(sort);

        // Section names come off the UNFILTERED rows, so a chip never disappears because of a
        // search term typed after it was chosen, and «Без раздела» stays available as a choice.
        var sectionNames = rows
            .Select(row => row.CategoryName ?? SectionWithoutName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToArray();

        if (rows.Count == 0) return ProductAnalyticsProjectionResult.Empty with { SectionNames = sectionNames };

        var visible = rows.Where(row => Matches(row, filter)).ToArray();
        var visibleQuantity = visible.Sum(row => row.Quantity);
        var visibleRevenue = Money.Round(visible.Sum(row => row.Revenue));
        var totalQuantity = rows.Sum(row => row.Quantity);

        return grouping switch
        {
            ProductAnalyticsGrouping.None => new ProductAnalyticsProjectionResult(
                [], [], OrderLines(visible, sort), sectionNames, visibleQuantity, visibleRevenue, totalQuantity),

            ProductAnalyticsGrouping.Dishes => new ProductAnalyticsProjectionResult(
                [], OrderDishes(GroupDishes(visible, sort), sort), [], sectionNames, visibleQuantity, visibleRevenue, totalQuantity),

            _ => BuildSections(visible, sort, sectionNames, visibleQuantity, visibleRevenue, totalQuantity)
        };
    }

    /// <summary>The name a dish with no section is filed under. A first-class bucket, not an omission.</summary>
    public const string SectionWithoutName = "Без раздела";

    /// <summary>Cuts the visible rows into sections, then into dishes inside each section, and orders both levels by the chosen criterion.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static ProductAnalyticsProjectionResult BuildSections(
        IReadOnlyList<ProductAnalyticsRowData> visible,
        ProductAnalyticsSort sort,
        IReadOnlyList<string> sectionNames,
        int visibleQuantity,
        decimal visibleRevenue,
        int totalQuantity)
    {
        var sections = visible
            .GroupBy(row => row.CategoryName ?? SectionWithoutName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var dishes = OrderDishes(GroupDishes(group.ToArray(), sort), sort);
                return new ProductAnalyticsSection(
                    group.Key,
                    dishes.Sum(dish => dish.Quantity),
                    Money.Round(dishes.Sum(dish => dish.Revenue)),
                    dishes);
            })
            .OrderBy(section => section, SectionComparer(sort))
            .ToArray();

        return new ProductAnalyticsProjectionResult(
            sections, [], [], sectionNames, visibleQuantity, visibleRevenue, totalQuantity);
    }

    /// <summary>Orders sections by the chosen criterion, tie-breaking on the name ALWAYS ascending — the same rule the dishes follow, for the same reason.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static IComparer<ProductAnalyticsSection> SectionComparer(ProductAnalyticsSort sort) =>
        sort.Criterion switch
        {
            ProductAnalyticsSortCriterion.Quantity => new SectionComparerAdapter(
                section => section.Quantity,
                section => section.Revenue,
                sort.Direction),
            ProductAnalyticsSortCriterion.Revenue => new SectionComparerAdapter(
                section => section.Revenue,
                section => section.Quantity,
                sort.Direction),
            _ => new SectionNameComparer(sort.Direction)
        };

    /// <summary>One dish per distinct name within the given rows, each carrying its lines ALREADY ordered by the same criterion the dishes themselves are.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static List<ProductAnalyticsDish> GroupDishes(IReadOnlyList<ProductAnalyticsRowData> rows, ProductAnalyticsSort sort) =>
        rows
            .GroupBy(row => row.ProductName, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ProductAnalyticsDish(
                group.First().ProductName,
                group.Sum(row => row.Quantity),
                Money.Round(group.Sum(row => row.Revenue)),
                OrderLines(group, sort)))
            .ToList();

    private static ProductAnalyticsLine[] OrderLines(
        IEnumerable<ProductAnalyticsRowData> rows,
        ProductAnalyticsSort sort) =>
        rows
            .Select(row => new ProductAnalyticsLine(row.ProductName, row.ModifierName, row.Quantity, row.Revenue))
            .OrderBy(line => line, LineComparer(sort))
            .ToArray();

    /// <summary>Orders dishes by the chosen criterion.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static ProductAnalyticsDish[] OrderDishes(IEnumerable<ProductAnalyticsDish> dishes, ProductAnalyticsSort sort) =>
        dishes
            .OrderBy(dish => dish, DishComparer(sort))
            .ToArray();

    private static IComparer<ProductAnalyticsDish> DishComparer(ProductAnalyticsSort sort) =>
        sort.Criterion switch
        {
            ProductAnalyticsSortCriterion.Quantity => new DishComparerAdapter(
                dish => dish.Quantity,
                dish => dish.Revenue,
                sort.Direction),
            ProductAnalyticsSortCriterion.Revenue => new DishComparerAdapter(
                dish => dish.Revenue,
                dish => dish.Quantity,
                sort.Direction),
            _ => new DishNameComparer(sort.Direction)
        };

    private static IComparer<ProductAnalyticsLine> LineComparer(ProductAnalyticsSort sort) =>
        sort.Criterion switch
        {
            ProductAnalyticsSortCriterion.Quantity => new LineComparerAdapter(
                line => line.Quantity,
                line => line.Revenue,
                sort.Direction),
            ProductAnalyticsSortCriterion.Revenue => new LineComparerAdapter(
                line => line.Revenue,
                line => line.Quantity,
                sort.Direction),
            _ => new LineNameComparer(sort.Direction)
        };

    private static bool Matches(ProductAnalyticsRowData row, ProductAnalyticsFilter filter)
    {
        /// <summary>An EMPTY section set means "no section filter", which is the state a cleared chip row leaves behind.</summary>
        /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

        if (filter.Sections.Count > 0 && !filter.Sections.Contains(row.CategoryId)) return false;

        if (!string.IsNullOrWhiteSpace(filter.Search) &&
            !row.ProductName.Contains(filter.Search.Trim(), StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private sealed class SectionComparerAdapter(
        Func<ProductAnalyticsSection, decimal> primary,
        Func<ProductAnalyticsSection, decimal> secondary,
        ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsSection>
    {
        public int Compare(ProductAnalyticsSection? x, ProductAnalyticsSection? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var primaryResult = primary(x).CompareTo(primary(y));
            if (primaryResult != 0) return direction == ProductAnalyticsSortDirection.Ascending ? primaryResult : -primaryResult;

            var secondaryResult = secondary(x).CompareTo(secondary(y));
            if (secondaryResult != 0) return secondaryResult;

            return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed class SectionNameComparer(ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsSection>
    {
        public int Compare(ProductAnalyticsSection? x, ProductAnalyticsSection? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var result = string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
            if (result != 0) return direction == ProductAnalyticsSortDirection.Ascending ? result : -result;

            return x.Quantity.CompareTo(y.Quantity);
        }
    }

    /// <summary>Orders a dish by the chosen measure, falling back to the name ALWAYS ascending.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private sealed class DishComparerAdapter(
        Func<ProductAnalyticsDish, decimal> primary,
        Func<ProductAnalyticsDish, decimal> secondary,
        ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsDish>
    {
        public int Compare(ProductAnalyticsDish? x, ProductAnalyticsDish? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var primaryResult = primary(x).CompareTo(primary(y));
            if (primaryResult != 0) return direction == ProductAnalyticsSortDirection.Ascending ? primaryResult : -primaryResult;

            var secondaryResult = secondary(x).CompareTo(secondary(y));
            if (secondaryResult != 0) return secondaryResult;

            return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed class DishNameComparer(ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsDish>
    {
        public int Compare(ProductAnalyticsDish? x, ProductAnalyticsDish? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var result = string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
            if (result != 0) return direction == ProductAnalyticsSortDirection.Ascending ? result : -result;

            return x.Quantity.CompareTo(y.Quantity);
        }
    }

    private sealed class LineComparerAdapter(
        Func<ProductAnalyticsLine, decimal> primary,
        Func<ProductAnalyticsLine, decimal> secondary,
        ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsLine>
    {
        public int Compare(ProductAnalyticsLine? x, ProductAnalyticsLine? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var primaryResult = primary(x).CompareTo(primary(y));
            if (primaryResult != 0) return direction == ProductAnalyticsSortDirection.Ascending ? primaryResult : -primaryResult;

            var secondaryResult = secondary(x).CompareTo(secondary(y));
            if (secondaryResult != 0) return secondaryResult;

            return string.Compare(x.ModifierName, y.ModifierName, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed class LineNameComparer(ProductAnalyticsSortDirection direction) : IComparer<ProductAnalyticsLine>
    {
        public int Compare(ProductAnalyticsLine? x, ProductAnalyticsLine? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var result = string.Compare(x.DishName, y.DishName, StringComparison.CurrentCultureIgnoreCase);
            if (result != 0) return direction == ProductAnalyticsSortDirection.Ascending ? result : -result;

            return string.Compare(x.ModifierName, y.ModifierName, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
