using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

/// <summary>
/// What the menu grid shows: which category, which hour, and what happens when the chosen category
/// stops existing.
/// </summary>
/// <remarks>
/// <para>
/// The two sentinel keys and the rule for a stale selection were properties of a chip row type in the
/// presentation layer, which meant neither could be asked a question from a test — and the stale-key
/// case is the one that happens on its own: Shell re-runs the load on every return to the tab, so a
/// chip can name a category that was deleted while the operator was elsewhere.
/// </para>
/// </remarks>
public class MenuFilterTests
{
    private static readonly Guid Desserts = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Drinks = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Category Category(Guid id, string name) => new() { Id = id, Name = name };

    private static Product Dish(Guid id, Guid? categoryId, int? fromHour = null, int? toHour = null) =>
        new() { Id = id, Name = id.ToString()[..4], CategoryId = categoryId, AvailableFromHour = fromHour, AvailableToHour = toHour };

    [Fact]
    public void The_two_sentinels_cannot_be_produced_by_a_generated_category_id()
    {
        // «Все» is Guid.Empty and «Комбо» ends in c0. The rule that repairs a stale selection is only
        // sound if no real category can ever equal either, and neither is a value NewGuid hands out.
        Assert.NotEqual(Guid.NewGuid(), MenuFilter.AllKey);
        Assert.NotEqual(Guid.NewGuid(), MenuFilter.CombosKey);
        Assert.NotEqual(MenuFilter.AllKey, MenuFilter.CombosKey);
    }

    [Fact]
    public void The_bundles_key_is_recognised_and_nothing_else_is()
    {
        Assert.True(MenuFilter.IsBundlesOnly(MenuFilter.CombosKey));

        // Including «Все», which is the filter that shows everything and must therefore not be mistaken
        // for the one that shows nothing.
        Assert.False(MenuFilter.IsBundlesOnly(MenuFilter.AllKey));
        Assert.False(MenuFilter.IsBundlesOnly(Desserts));
        Assert.False(MenuFilter.IsBundlesOnly(null));
    }

    [Fact]
    public void A_key_the_strip_still_offers_is_kept()
    {
        var keys = new[] { MenuFilter.AllKey, Desserts, MenuFilter.CombosKey };

        Assert.Equal(Desserts, MenuFilter.Repair(Desserts, keys));
        Assert.Equal(MenuFilter.CombosKey, MenuFilter.Repair(MenuFilter.CombosKey, keys));
    }

    [Fact]
    public void A_category_deleted_while_the_page_was_closed_falls_back_to_showing_everything()
    {
        // THE CASE. The operator picked «Десерты», went to another tab, and the category was deleted
        // there. On return the key names a chip that is not on the strip: highlighting nothing and
        // filtering by nothing would leave the grid empty for a reason nobody can see.
        var keys = new[] { MenuFilter.AllKey, Drinks, MenuFilter.CombosKey };

        Assert.Equal(MenuFilter.AllKey, MenuFilter.Repair(Desserts, keys));
    }

    [Fact]
    public void A_key_that_was_never_valid_falls_back_too()
    {
        var keys = new[] { MenuFilter.AllKey, Drinks, MenuFilter.CombosKey };

        Assert.Equal(MenuFilter.AllKey, MenuFilter.Repair(Guid.NewGuid(), keys));
    }

    [Fact]
    public void An_empty_strip_still_leaves_the_key_readable()
    {
        // No categories at all, and the key still points at something a caller can rely on rather than
        // at a chip that does not exist.
        Assert.Equal(MenuFilter.AllKey, MenuFilter.Repair(Desserts, []));
        Assert.Equal(MenuFilter.AllKey, MenuFilter.Repair(MenuFilter.AllKey, []));
    }

    [Fact]
    public void No_category_selected_shows_every_dish()
    {
        var desserts = Category(Desserts, "Десерты");
        var dishes = new[] { Dish(Guid.NewGuid(), Desserts), Dish(Guid.NewGuid(), Drinks) };

        var visible = MenuFilter.Apply(dishes, category: null, localHour: 12);

        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public void A_selected_category_narrows_the_grid_to_it()
    {
        var desserts = Category(Desserts, "Десерты");
        var dishes = new[] { Dish(Guid.NewGuid(), Desserts), Dish(Guid.NewGuid(), Drinks) };

        var visible = MenuFilter.Apply(dishes, desserts, localHour: 12);

        Assert.Equal(desserts.Id, Assert.Single(visible).CategoryId);
    }

    [Fact]
    public void A_dish_outside_its_time_window_is_not_offered_even_in_its_own_category()
    {
        // Served from 08:00 to 20:00, looked at at 21:00. Shown as unavailable it would be a tile the
        // cashier can only refuse; hidden, it is simply not on the board at that hour.
        var desserts = Category(Desserts, "Десерты");
        var early = Dish(Guid.NewGuid(), Desserts, fromHour: 8, toHour: 20);
        var late = Dish(Guid.NewGuid(), Desserts, fromHour: 20, toHour: 23);

        Assert.Empty(MenuFilter.Apply([early], desserts, localHour: 21));
        Assert.Single(MenuFilter.Apply([late], desserts, localHour: 21));
    }

    [Fact]
    public void A_dish_with_no_window_is_always_on_the_board()
    {
        var desserts = Category(Desserts, "Десерты");
        var allDay = Dish(Guid.NewGuid(), desserts.Id);

        Assert.Single(MenuFilter.Apply([allDay], desserts, localHour: 3));
        Assert.Single(MenuFilter.Apply([allDay], desserts, localHour: 23));
    }

    [Fact]
    public void A_hour_outside_the_clock_is_rejected_rather_than_silently_filtering_everything_out()
    {
        // A caller with a broken clock gets an empty grid and no idea why. Refusing the argument is the
        // one outcome that cannot reach a screen.
        var dishes = new[] { Dish(Guid.NewGuid(), Desserts) };

        Assert.Throws<ArgumentOutOfRangeException>(() => MenuFilter.Apply(dishes, Category(Desserts, "Д"), localHour: 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => MenuFilter.Apply(dishes, Category(Desserts, "Д"), localHour: -1));
    }

    [Fact]
    public void A_category_with_no_dishes_yields_an_empty_grid_rather_than_throwing()
    {
        // Selecting a category the menu does not stock is ordinary — a café adds the chip before the
        // first dish is entered — and it must not be an error.
        Assert.Empty(MenuFilter.Apply([], Category(Desserts, "Десерты"), localHour: 12));
    }
}