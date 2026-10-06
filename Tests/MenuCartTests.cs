using CafePos.Core.Models;
using CafePos.Presentation.Services;
using CafePos.Presentation.ViewModels;

namespace CafePosApp.Tests;

/// <summary>
/// The cart: what the operator's taps do to it, before phase 2 splits the class that owns it.
/// </summary>
/// <remarks>
/// <para>
/// Characterisation tests, same as <see cref="CartLineMergeTests"/>, and for the same reason: this is
/// the net to catch a bad move, not a specification to catch a change of mind. Every assertion here is
/// about behaviour that is currently correct.
/// </para>
/// <para>
/// <b>NOTHING IS MOCKED.</b> The catalog, combos and draft store are the real services over a real
/// database. Only the sheets are replaced — those need a window — and they RECORD what they were asked,
/// so "the variant picker did not open for a dish with no variants" is an assertable claim. That is the
/// failure worth protecting: a dish with variants that silently sells the wrong size shows up nowhere
/// else.
/// </para>
/// </remarks>
public class MenuCartTests
{
    [Fact]
    public async Task Loading_fills_the_grid_and_the_strip()
    {
        using var harness = await MenuHarness.CreateAsync();
        var desserts = await harness.AddCategoryAsync("Десерты");
        await harness.AddProductAsync("Чизкейк", 280m, desserts.Id);
        await harness.AddProductAsync("Латте", 220m);

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.ViewModel.FilteredProducts.Count);

        // The synthetic "Все" chip is part of the strip, so the whole thing is one scrolling list
        // rather than a lone button beside a collection.
        Assert.Contains(harness.ViewModel.Categories, chip => chip.IsAll);
        Assert.Contains(harness.ViewModel.Categories, chip => chip.Name == "Десерты");
    }

    [Fact]
    public async Task Adding_a_dish_puts_one_line_in_the_cart()
    {
        using var harness = await MenuHarness.CreateAsync();
        var latte = await harness.AddProductAsync("Латте", 220m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);

        var line = Assert.Single(harness.ViewModel.Cart);
        Assert.Equal("Латте", line.ProductName);
        Assert.Equal(1, line.Quantity);
        Assert.Equal(220m, harness.ViewModel.Total);
    }

    [Fact]
    public async Task Adding_the_same_dish_twice_raises_the_quantity_and_leaves_one_row()
    {
        using var harness = await MenuHarness.CreateAsync();
        var latte = await harness.AddProductAsync("Латте", 220m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);
        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);

        var line = Assert.Single(harness.ViewModel.Cart);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(440m, harness.ViewModel.Total);
    }

    [Fact]
    public async Task Removing_a_line_empties_the_cart()
    {
        using var harness = await MenuHarness.CreateAsync();
        var latte = await harness.AddProductAsync("Латте", 220m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);

        harness.ViewModel.RemoveItemCommand.Execute(harness.ViewModel.Cart[0]);

        Assert.Empty(harness.ViewModel.Cart);
        Assert.Equal(0m, harness.ViewModel.Total);
    }

    [Fact]
    public async Task A_removed_line_can_be_brought_back_where_it_was()
    {
        // Undo restores the INDEX, not just the item: two rows of the same dish are two rows, and
        // putting the removed one back at the end would reorder a cart the cashier has already read.
        using var harness = await MenuHarness.CreateAsync();
        var tea = await harness.AddProductAsync("Чай", 140m);
        var coffee = await harness.AddProductAsync("Кофе", 300m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        await harness.ViewModel.AddProductCommand.ExecuteAsync(tea);
        await harness.ViewModel.AddProductCommand.ExecuteAsync(coffee);

        var removed = harness.ViewModel.Cart[0];
        harness.ViewModel.RemoveItemCommand.Execute(removed);
        harness.ViewModel.UndoRemoveCommand.Execute(null);

        Assert.Equal(2, harness.ViewModel.Cart.Count);
        Assert.Equal("Чай", harness.ViewModel.Cart[0].ProductName);
    }

    [Fact]
    public async Task Choosing_a_category_narrows_the_grid_to_it()
    {
        using var harness = await MenuHarness.CreateAsync();
        var desserts = await harness.AddCategoryAsync("Десерты");
        await harness.AddProductAsync("Чизкейк", 280m, desserts.Id);
        await harness.AddProductAsync("Латте", 220m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        var chip = harness.ViewModel.Categories.Single(c => c.Name == "Десерты");
        harness.ViewModel.SelectCategoryCommand.Execute(chip);

        Assert.Equal("Чизкейк", Assert.Single(harness.ViewModel.FilteredProducts).Name);
    }

    [Fact]
    public async Task A_dish_with_variants_asks_which_one_and_never_opens_the_picker_for_one_without()
    {
        // The asymmetry is the point. Opening a picker for a dish that has no variants would put an
        // empty dialog in front of the cashier; NOT opening it for one that has them sells the wrong
        // size silently. Both are wrong, in opposite directions, and neither crashes.
        using var harness = await MenuHarness.CreateAsync();
        var sized = await harness.AddProductAsync("Кофе", 300m, hasVariants: true);
        var plain = await harness.AddProductAsync("Чай", 140m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(plain);
        Assert.Equal(0, harness.VariantPicker.Opened);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(sized);
        Assert.Equal(1, harness.VariantPicker.Opened);
    }

    [Fact]
    public async Task An_unavailable_dish_is_shown_as_a_state_and_adds_nothing()
    {
        // Show-unavailable-not-action: the tile is visible so the cashier can see the dish exists and
        // has run out, but tapping it must not put a line in the cart.
        using var harness = await MenuHarness.CreateAsync();
        var soldOut = await harness.AddProductAsync("Мороженое", 150m);
        await harness.Catalog.SaveProductAsync(new Product
        {
            Id = soldOut.Id,
            Name = soldOut.Name,
            Price = soldOut.Price,
            IsAvailable = false,
        });
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        var shown = harness.ViewModel.FilteredProducts.Single(p => p.Id == soldOut.Id);
        Assert.Equal("Мороженое", shown.Name);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(shown);

        // Visible, but tapping it must not sell it.
        Assert.Empty(harness.ViewModel.Cart);
    }

    [Fact]
    public async Task The_cart_starts_as_counter_service_and_the_toggle_moves_it()
    {
        using var harness = await MenuHarness.CreateAsync();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsTakeaway);

        harness.ViewModel.ToggleOrderTypeCommand.Execute(null);

        Assert.True(harness.ViewModel.IsTakeaway);
    }

    [Fact]
    public async Task A_promised_time_is_kept_as_named_and_a_dismissed_picker_changes_nothing()
    {
        // The two are both "no time in RequestedAt" at the Core, and only the record's discriminant
        // tells them apart - a dismissal that fell through as "ASAP" would silently rewrite the promise
        // the customer was given. What must not change is that dismissing the control leaves it reading
        // what it read before, so this asserts against the value captured beforehand rather than a
        // literal: I guessed "null" when writing the first version of this test and was wrong.
        using var dismissed = await MenuHarness.CreateAsync();
        await dismissed.ViewModel.LoadCommand.ExecuteAsync(null);
        var before = dismissed.ViewModel.RequestedTimeText;
        await dismissed.ViewModel.PickTimeCommand.ExecuteAsync(null);
        Assert.Equal(before, dismissed.ViewModel.RequestedTimeText);

        using var named = await MenuHarness.CreateAsync(timePickAnswer: TimePickResult.At(new TimeSpan(18, 30, 0)));
        await named.ViewModel.LoadCommand.ExecuteAsync(null);
        await named.ViewModel.PickTimeCommand.ExecuteAsync(null);

        // 18:30 on a clock fixed at 17:00 is a promise still to keep, not a late one.
        Assert.Equal("18:30", named.ViewModel.RequestedTimeText);
        Assert.False(named.ViewModel.IsRequestedTimeLate);
    }

    [Fact]
    public async Task No_dialog_is_raised_by_ordinary_tapping()
    {
        // A cashier building an order must never be interrupted by a confirmation they did not ask for.
        using var harness = await MenuHarness.CreateAsync();
        var latte = await harness.AddProductAsync("Латте", 220m);
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);
        await harness.ViewModel.AddProductCommand.ExecuteAsync(latte);

        Assert.Empty(harness.Dialogs.Prompted);
    }
}
