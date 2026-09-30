using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// The catalogue row overflow menu. Every row action moved out of the row and into a sheet, so what
/// the sheet contains — and which entry is destructive — is now behaviour worth pinning down.
/// </summary>
public class CatalogActionsTests
{
    private static CatalogAction? Find(IReadOnlyList<CatalogAction> actions, string key) =>
        actions.FirstOrDefault(action => action.Key == key);

    [Fact]
    public void Product_offers_edit_copy_and_delete()
    {
        var product = new Product { Name = "Латте", IsAvailable = true, IsDeleted = false };

        var actions = CatalogActions.ForProduct(product);

        Assert.Equal(
            [CatalogActionKeys.Edit, CatalogActionKeys.Copy, CatalogActionKeys.ToggleAvailability, CatalogActionKeys.Delete],
            actions.Select(action => action.Key));
        Assert.True(Find(actions, CatalogActionKeys.Delete)!.IsDestructive);
        Assert.All(actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Title)));
    }

    [Fact]
    public void Deleted_product_offers_restore_instead_of_delete()
    {
        var product = new Product { Name = "Латте", IsDeleted = true };

        var actions = CatalogActions.ForProduct(product);

        Assert.NotNull(Find(actions, CatalogActionKeys.Restore));
        Assert.Null(Find(actions, CatalogActionKeys.Delete));
        // Restoring is not destructive, so it must not be styled as such.
        Assert.False(Find(actions, CatalogActionKeys.Restore)!.IsDestructive);
    }

    [Theory]
    [InlineData(true, "Снять с продажи")]
    [InlineData(false, "Вернуть в продажу")]
    public void Availability_entry_names_the_next_action_not_the_current_state(bool isAvailable, string expected)
    {
        var product = new Product { Name = "Латте", IsAvailable = isAvailable };

        var availability = Find(CatalogActions.ForProduct(product), CatalogActionKeys.ToggleAvailability);

        Assert.Equal(expected, availability!.Title);
    }

    [Theory]
    [InlineData(true, "Снять с продажи")]
    [InlineData(false, "Вернуть в продажу")]
    public void Ingredient_availability_reads_the_same_way(bool isAvailable, string expected)
    {
        var ingredient = new Ingredient { Name = "Молоко", IsAvailable = isAvailable };

        var availability = Find(CatalogActions.ForIngredient(ingredient), CatalogActionKeys.ToggleAvailability);

        Assert.Equal(expected, availability!.Title);
    }

    [Fact]
    public void Modifier_option_can_be_hidden_or_removed()
    {
        var option = new ModifierOption { Name = "Овсяное", IsAvailable = true };

        var actions = CatalogActions.ForModifierOption(option);

        Assert.Equal(
            [CatalogActionKeys.ToggleAvailability, CatalogActionKeys.RemoveFromGroup],
            actions.Select(action => action.Key));
        Assert.True(Find(actions, CatalogActionKeys.RemoveFromGroup)!.IsDestructive);
    }

    [Fact]
    public void Category_and_group_rename_rather_than_edit()
    {
        Assert.Contains(CatalogActions.ForCategory(), action => action.Title == "Переименовать");
        Assert.Contains(CatalogActions.ForModifierGroup(), action => action.Title == "Переименовать группу");
    }

    [Fact]
    public void No_sheet_offers_more_than_one_destructive_entry()
    {
        var sheets = new[]
        {
            CatalogActions.ForProduct(new Product { Name = "Латте" }),
            CatalogActions.ForProduct(new Product { Name = "Латте", IsDeleted = true }),
            CatalogActions.ForCategory(),
            CatalogActions.ForModifierGroup(),
            CatalogActions.ForModifierOption(new ModifierOption { Name = "Овсяное" }),
            CatalogActions.ForIngredient(new Ingredient { Name = "Молоко" })
        };

        foreach (var sheet in sheets)
        {
            Assert.InRange(sheet.Count(action => action.IsDestructive), 0, 1);
            Assert.All(sheet.Select(action => action.Key), key => Assert.False(string.IsNullOrWhiteSpace(key)));
        }
    }

    [Fact]
    public void A_deleted_product_sheet_has_nothing_destructive_left_to_do()
    {
        var sheet = CatalogActions.ForProduct(new Product { Name = "Латте", IsDeleted = true });

        Assert.Empty(sheet.Where(action => action.IsDestructive));
    }
}
