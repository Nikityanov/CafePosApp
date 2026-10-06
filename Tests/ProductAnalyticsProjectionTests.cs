using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Tests for <see cref="ProductAnalyticsProjection"/> — the filtering, sorting and grouping of one
/// shift's product breakdown.
/// </summary>
/// <remarks>
/// Every case here is a rule a manager would notice being wrong: a dish that lost its modifiers, a
/// section that swallowed a dish from another one, a filter that cannot be undone. The fixtures are
/// built by <see cref="Shift"/> so a test reads as the situation it describes rather than as a
/// pile of constructor arguments.
/// </remarks>
public class ProductAnalyticsProjectionTests
{
    private static readonly Guid Coffee = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Food = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ProductAnalyticsRowData Row(
        string product,
        string modifier,
        int quantity,
        decimal revenue,
        Guid? sectionId = null,
        string? sectionName = null) =>
        new(product, modifier, quantity, revenue, sectionId, sectionName);

    /// <summary>
    /// The fixture every case starts from: two sections, three dishes, one dish sold with two
    /// modifiers and one dish in no section at all.
    /// </summary>
    private static List<ProductAnalyticsRowData> Shift() =>
    [
        Row("Латте", "Без модификатора", 10, 2000m, Coffee, "Кофе"),
        Row("Латте", "Овсяное", 4, 900m, Coffee, "Кофе"),
        Row("Эспрессо", "Без модификатора", 3, 450m, Coffee, "Кофе"),
        Row("Чизкейк", "Без модификатора", 5, 1500m, Food, "Еда"),
        Row("Суп", "Без модификатора", 2, 1000m, null, null),
    ];

    private static ProductAnalyticsProjectionResult Build(
        List<ProductAnalyticsRowData>? rows = null,
        ProductAnalyticsFilter? filter = null,
        ProductAnalyticsSort? sort = null,
        ProductAnalyticsGrouping grouping = ProductAnalyticsGrouping.SectionsAndDishes) =>
        ProductAnalyticsProjection.Build(
            rows ?? Shift(),
            filter ?? ProductAnalyticsFilter.Empty,
            sort ?? ProductAnalyticsSort.ByQuantityDescending,
            grouping);

    // ── Grouping shapes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Flat_grouping_returns_one_row_per_dish_and_modifier()
    {
        var result = Build(grouping: ProductAnalyticsGrouping.None);

        Assert.Equal(5, result.Lines.Count);
        Assert.Empty(result.Sections);
        Assert.Empty(result.Dishes);
    }

    [Fact]
    public void Dish_grouping_collects_the_modifiers_of_one_dish_under_it()
    {
        var result = Build(grouping: ProductAnalyticsGrouping.Dishes);

        var latte = Assert.Single(result.Dishes, dish => dish.Name == "Латте");
        Assert.Equal(14, latte.Quantity);
        Assert.Equal(2900m, latte.Revenue);
        // The dish's OWN lines follow the same criterion as the dish: ten plain lattes outrank four
        // oat ones, so the plain line leads under the default descending quantity order.
        Assert.Equal(2, latte.Lines.Count);
        Assert.Equal(["Без модификатора", "Овсяное"], latte.Lines.Select(line => line.ModifierName));
    }

    [Fact]
    public void Section_grouping_files_each_dish_under_its_own_section()
    {
        var result = Build();

        Assert.Equal(3, result.Sections.Count);
        var coffee = Assert.Single(result.Sections, section => section.Name == "Кофе");
        Assert.Equal(17, coffee.Quantity);
        Assert.Equal(3350m, coffee.Revenue);
        Assert.Equal(["Латте", "Эспрессо"], coffee.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void A_dish_with_no_section_gets_its_own_section_rather_than_disappearing()
    {
        var result = Build();

        var orphan = Assert.Single(result.Sections, section => section.Name == ProductAnalyticsProjection.SectionWithoutName);
        Assert.Equal("Суп", Assert.Single(orphan.Dishes).Name);
        Assert.Contains(ProductAnalyticsProjection.SectionWithoutName, result.SectionNames);
    }

    [Fact]
    public void Section_totals_are_the_sum_of_the_dishes_they_contain()
    {
        var result = Build();

        foreach (var section in result.Sections)
        {
            Assert.Equal(section.Dishes.Sum(dish => dish.Quantity), section.Quantity);
            Assert.Equal(Money(section.Dishes.Sum(dish => dish.Revenue)), section.Revenue);
        }
    }

    [Fact]
    public void The_same_dish_name_in_two_sections_stays_two_dishes()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Латте", "Без модификатора", 1, 100m, Coffee, "Кофе"),
            Row("Латте", "Без модификатора", 2, 200m, Food, "Еда"),
        };

        var result = Build(rows);

        var coffee = Assert.Single(result.Sections, section => section.Name == "Кофе");
        var food = Assert.Single(result.Sections, section => section.Name == "Еда");
        Assert.Equal(100m, Assert.Single(coffee.Dishes).Revenue);
        Assert.Equal(200m, Assert.Single(food.Dishes).Revenue);
    }

    [Fact]
    public void Sections_follow_the_criterion_while_the_chip_strip_stays_alphabetical()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Суп", "Без модификатора", 9, 100m, Food, "Еда"),
            Row("Чай", "Без модификатора", 1, 100m, Coffee, "Азия"),
        };

        var result = Build(rows);

        // The LIST orders by the criterion: nine soups outrank one tea, so «Еда» leads even though
        // «Азия» comes first alphabetically.
        Assert.Equal(["Еда", "Азия"], result.Sections.Select(section => section.Name));

        // The CHIPS are a fixed alphabetical strip regardless of the sort, because a filter strip
        // that reorders itself under the reader's finger cannot be learned.
        Assert.Equal(["Азия", "Еда"], result.SectionNames);
    }

    // ── Section names ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Section_names_come_from_the_unfiltered_rows()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { Coffee }, null);

        var result = Build(filter: filter);

        // Only «Кофе» survives the filter, yet every section stays offered — a chip strip built from
        // what is visible would make the other sections unselectable.
        Assert.Equal(3, result.SectionNames.Count);
        Assert.Single(result.Sections);
    }

    [Fact]
    public void Section_names_are_listed_once_each_even_with_many_rows()
    {
        var result = Build();

        Assert.Equal(result.SectionNames.Count, result.SectionNames.Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
    }

    // ── Filtering ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_section_filter_keeps_every_section()
    {
        var result = Build(filter: ProductAnalyticsFilter.Empty);

        Assert.Equal(17 + 5 + 2, result.VisibleQuantity);
        Assert.Equal(24, result.TotalQuantity);
    }

    [Fact]
    public void Selecting_a_section_keeps_only_its_rows()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { Food }, null);

        var result = Build(filter: filter);

        var food = Assert.Single(result.Sections);
        Assert.Equal("Еда", food.Name);
        Assert.Equal("Чизкейк", Assert.Single(food.Dishes).Name);
        Assert.Equal(5, result.VisibleQuantity);
        Assert.Equal(1500m, result.VisibleRevenue);
        Assert.Equal(24, result.TotalQuantity);
    }

    [Fact]
    public void Selecting_several_sections_is_their_union()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { Coffee, Food }, null);

        var result = Build(filter: filter);

        Assert.Equal(22, result.VisibleQuantity);
        // Ordered by the criterion, so «Кофе» (17 units) precedes «Еда» (5) rather than sitting
        // where the alphabet happens to put it.
        Assert.Equal(["Кофе", "Еда"], result.Sections.Select(section => section.Name));
    }

    [Fact]
    public void The_no_section_bucket_is_selectable_like_any_other_section()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { null }, null);

        var result = Build(filter: filter);

        var orphan = Assert.Single(result.Sections);
        Assert.Equal(ProductAnalyticsProjection.SectionWithoutName, orphan.Name);
        Assert.Equal(2, result.VisibleQuantity);
    }

    [Fact]
    public void Search_keeps_only_dishes_whose_name_contains_the_term()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?>(), "лат");

        var result = Build(filter: filter, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal("Латте", Assert.Single(result.Dishes).Name);
        Assert.Equal(14, result.VisibleQuantity);
        Assert.Equal(24, result.TotalQuantity);
    }

    [Fact]
    public void Search_ignores_surrounding_whitespace_and_case()
    {
        var rows = new List<ProductAnalyticsRowData> { Row("Чизкейк", "Без модификатора", 1, 100m) };

        var padded = Build(rows, new ProductAnalyticsFilter(new HashSet<Guid?>(), "  ЧИЗ "));
        var exact = Build(rows, new ProductAnalyticsFilter(new HashSet<Guid?>(), "Чиз"));

        Assert.Equal(1, padded.VisibleQuantity);
        Assert.Equal(exact.VisibleQuantity, padded.VisibleQuantity);
    }

    [Fact]
    public void A_search_that_matches_nothing_leaves_the_shift_total_intact()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?>(), "ZZZ");

        var result = Build(filter: filter);

        Assert.Empty(result.Sections);
        Assert.Empty(result.Dishes);
        Assert.Empty(result.Lines);
        Assert.Equal(0, result.VisibleQuantity);
        Assert.Equal(24, result.TotalQuantity);
        Assert.True(result.IsFilteredToNothing);
    }

    [Fact]
    public void A_shift_that_sold_nothing_is_not_reported_as_filtered_to_nothing()
    {
        var result = Build(rows: []);

        Assert.Empty(result.Sections);
        Assert.Equal(0, result.TotalQuantity);
        Assert.False(result.IsFilteredToNothing);
    }

    [Fact]
    public void Section_and_search_filters_are_combined_as_and()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { Coffee }, "лат");

        var result = Build(filter: filter);

        var coffee = Assert.Single(result.Sections);
        Assert.Equal("Кофе", coffee.Name);
        Assert.Equal("Латте", Assert.Single(coffee.Dishes).Name);
        Assert.Equal(14, result.VisibleQuantity);
        Assert.Equal(24, result.TotalQuantity);
    }

    [Fact]
    public void Visible_totals_count_units_and_the_shift_total_does_not_move_with_the_filter()
    {
        var filter = new ProductAnalyticsFilter(new HashSet<Guid?> { Coffee }, null);

        var result = Build(filter: filter);

        Assert.Equal(17, result.VisibleQuantity);
        Assert.Equal(3350m, result.VisibleRevenue);
        Assert.Equal(24, result.TotalQuantity);
    }

    // ── Sorting ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dishes_sort_by_quantity_descending_by_default()
    {
        var result = Build(grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(["Латте", "Чизкейк", "Эспрессо", "Суп"], result.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void Dishes_sort_by_quantity_ascending_when_asked()
    {
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Quantity, ProductAnalyticsSortDirection.Ascending);

        var result = Build(sort: sort, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(["Суп", "Эспрессо", "Чизкейк", "Латте"], result.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void Sorting_by_revenue_can_rank_dishes_differently_from_their_quantity()
    {
        // «Суп» is bought twice for 1000 while «Эспрессо» is bought three times for 450: by money the
        // soup leads, by count the espresso does. This is the case that makes the criterion worth
        // having at all.
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Revenue, ProductAnalyticsSortDirection.Descending);

        var result = Build(sort: sort, grouping: ProductAnalyticsGrouping.Dishes);

        // By money: Латте 2900, Чизкейк 1500, Суп 1000, Эспрессо 450.
        Assert.Equal(["Латте", "Чизкейк", "Суп", "Эспрессо"], result.Dishes.Select(dish => dish.Name));

        // By count the two 2-unit-and-3-unit dishes swap ends relative to the money order, which is
        // the whole reason a revenue criterion exists.
        var byQuantity = Build(grouping: ProductAnalyticsGrouping.Dishes);
        Assert.Equal(["Латте", "Чизкейк", "Эспрессо", "Суп"], byQuantity.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void Sorting_by_name_follows_the_current_culture_rather_than_code_points()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Йогурт", "Без модификатора", 1, 100m),
            Row("Ежевика", "Без модификатора", 1, 100m),
            Row("Абрикос", "Без модификатора", 1, 100m),
        };
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Name, ProductAnalyticsSortDirection.Ascending);

        var result = Build(rows, sort: sort, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(["Абрикос", "Ежевика", "Йогурт"], result.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void Dishes_tied_on_the_chosen_measure_stay_in_name_order_in_both_directions()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Чай", "Без модификатора", 5, 500m),
            Row("Борщ", "Без модификатора", 5, 500m),
            Row("Абрикос", "Без модификатора", 5, 500m),
        };
        var descending = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Quantity, ProductAnalyticsSortDirection.Descending);
        var ascending = descending.Reversed();

        var byQuantity = Build(rows, sort: descending, grouping: ProductAnalyticsGrouping.Dishes);
        var reversed = Build(rows, sort: ascending, grouping: ProductAnalyticsGrouping.Dishes);

        // All three tie at 5 units, so the tie-break decides in BOTH directions and the name order
        // is identical either way. If this ever asserts different lists, the tie-break has started
        // following the direction — which is the defect this test exists to catch, because a list
        // that reshuffles its ties on every tap reads as noise.
        Assert.Equal(["Абрикос", "Борщ", "Чай"], byQuantity.Dishes.Select(dish => dish.Name));
        Assert.Equal(["Абрикос", "Борщ", "Чай"], reversed.Dishes.Select(dish => dish.Name));
    }

    [Fact]
    public void Sections_are_ordered_by_the_same_measure_the_dishes_are()
    {
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Quantity, ProductAnalyticsSortDirection.Descending);

        var result = Build(sort: sort);

        Assert.Equal(["Кофе", "Еда", ProductAnalyticsProjection.SectionWithoutName], result.Sections.Select(section => section.Name));
    }

    [Fact]
    public void A_dishs_own_lines_are_ordered_by_the_chosen_measure_too()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Латте", "Сироп", 1, 100m),
            Row("Латте", "Овсяное", 9, 900m),
        };
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Quantity, ProductAnalyticsSortDirection.Descending);

        var result = Build(rows, sort: sort, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(["Овсяное", "Сироп"], Assert.Single(result.Dishes).Lines.Select(line => line.ModifierName));
    }

    [Fact]
    public void Flat_rows_sort_by_the_chosen_measure()
    {
        var sort = new ProductAnalyticsSort(ProductAnalyticsSortCriterion.Revenue, ProductAnalyticsSortDirection.Descending);

        var result = Build(sort: sort, grouping: ProductAnalyticsGrouping.None);

        // Flat rows are per MODIFIER, so the largest single line is the ten plain lattes at 2000 —
        // not the dish's 2900 total, which only exists once the modifiers are grouped.
        Assert.Equal(2000m, result.Lines[0].Revenue);
        Assert.Equal("Латте", result.Lines[0].DishName);
        Assert.Equal(5, result.Lines.Count);
    }

    [Fact]
    public void Reversed_flips_only_the_direction()
    {
        var ascending = ProductAnalyticsSort.ByQuantityDescending.Reversed();

        Assert.Equal(ProductAnalyticsSortCriterion.Quantity, ascending.Criterion);
        Assert.Equal(ProductAnalyticsSortDirection.Ascending, ascending.Direction);
        Assert.Equal(ProductAnalyticsSort.ByQuantityDescending, ascending.Reversed());
    }

    // ── Guards ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_rejects_a_null_row_list()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProductAnalyticsProjection.Build(null!, ProductAnalyticsFilter.Empty, ProductAnalyticsSort.ByQuantityDescending, ProductAnalyticsGrouping.Dishes));
    }

    [Fact]
    public void Build_rejects_a_null_filter_and_a_null_sort()
    {
        var rows = Shift();

        Assert.Throws<ArgumentNullException>(() =>
            ProductAnalyticsProjection.Build(rows, null!, ProductAnalyticsSort.ByQuantityDescending, ProductAnalyticsGrouping.Dishes));
        Assert.Throws<ArgumentNullException>(() =>
            ProductAnalyticsProjection.Build(rows, ProductAnalyticsFilter.Empty, null!, ProductAnalyticsGrouping.Dishes));
    }

    [Fact]
    public void An_empty_shift_yields_an_empty_result_with_no_sections_offered()
    {
        var result = Build(rows: []);

        Assert.Empty(result.Sections);
        Assert.Empty(result.Dishes);
        Assert.Empty(result.Lines);
        Assert.Empty(result.SectionNames);
        Assert.Equal(0, result.VisibleRevenue);
    }

    [Fact]
    public void Dish_and_line_revenue_are_rounded_to_the_domain_precision()
    {
        var rows = new List<ProductAnalyticsRowData> { Row("Латте", "Без модификатора", 3, 10.005m) };

        var result = Build(rows, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(10.01m, Assert.Single(result.Dishes).Revenue);
    }

    [Fact]
    public void Dish_names_are_matched_case_insensitively_so_one_dish_is_not_split_in_two()
    {
        var rows = new List<ProductAnalyticsRowData>
        {
            Row("Латте", "Без модификатора", 2, 200m),
            Row("ЛАТТЕ", "Овсяное", 3, 300m),
        };

        var result = Build(rows, grouping: ProductAnalyticsGrouping.Dishes);

        Assert.Equal(5, Assert.Single(result.Dishes).Quantity);
    }

    private static decimal Money(decimal value) => CafePos.Core.Common.Money.Round(value);
}
