using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.ViewModels;

/// <summary>List mutations of the catalogue management screen.</summary>
public partial class CatalogManagementViewModel
{
    private async Task DeleteProductAsync(Product? product)
    {
        if (product is null) return;
        var confirmed = await dialogs.ConfirmAsync(
            "Удалить товар?",
            $"«{product.Name}» исчезнет из меню, но останется в истории заказов.",
            "Удалить");
        if (!confirmed) return;

        await catalog.DeleteProductAsync(product.Id);
        haptics.Click();
        await LoadAsync();
    }

    private async Task RestoreProductAsync(Product? product)
    {
        if (product is null) return;
        await catalog.RestoreProductAsync(product.Id);
        haptics.Click();
        await LoadAsync();
    }

    private async Task ToggleProductAvailabilityAsync(Product? product)
    {
        if (product is null) return;
        await catalog.ToggleProductAvailabilityAsync(product.Id);
        await LoadAsync();
    }

    private async Task CopyProductAsync(Product? source)
    {
        if (source is null) return;

        var copy = new Product
        {
            Id = Guid.NewGuid(),
            Name = $"{source.Name} (копия)",
            Price = source.Price,
            IsAvailable = false,
            CategoryId = source.CategoryId,
            ModifierGroupId = source.ModifierGroupId,
            HasVariants = source.HasVariants,
            PhotoPath = source.PhotoPath,
            Allergens = source.Allergens,
            Tags = source.Tags,
            AvailableFromHour = source.AvailableFromHour,
            AvailableToHour = source.AvailableToHour,
            Variants = [.. source.Variants.Select(variant => new ProductVariant
            {
                Id = Guid.NewGuid(),
                Name = variant.Name,
                Price = variant.Price,
                IsAvailable = variant.IsAvailable,
                SortOrder = variant.SortOrder
            })]
        };

        await catalog.SaveProductAsync(copy);
        SetNotice($"Скопировано: {copy.Name}", NoticeLevel.Success);
        haptics.Click();
        await LoadAsync();
    }

    private async Task DeleteCategoryAsync(Category? category)
    {
        if (category is null) return;
        var confirmed = await dialogs.ConfirmAsync(
            "Удалить раздел?",
            $"Раздел «{category.Name}» будет удалён. Товары раздела останутся без раздела.",
            "Удалить");
        if (!confirmed) return;

        await catalog.DeleteCategoryAsync(category.Id);
        haptics.Click();
        await LoadAsync();
    }

    private async Task DeleteModifierGroupAsync(ModifierOptionGroup? group)
    {
        if (group is null) return;
        var confirmed = await dialogs.ConfirmAsync(
            "Удалить группу?",
            $"Группа «{group.Name}» и все её варианты будут удалены.",
            "Удалить");
        if (!confirmed) return;

        await catalog.DeleteModifierGroupAsync(group.GroupId);
        haptics.Click();
        await LoadAsync();
    }

    private async Task ToggleModifierOptionAvailabilityAsync(ModifierOption? option)
    {
        if (option is null) return;
        await catalog.ToggleModifierOptionAvailabilityAsync(option.Id);
        await LoadAsync();
    }

    private async Task RemoveModifierOptionAsync(ModifierOption? option)
    {
        if (option is null) return;
        var confirmed = await dialogs.ConfirmAsync(
            "Удалить вариант?",
            $"«{option.Name}» будет убран из группы модификаторов.",
            "Удалить");
        if (!confirmed) return;

        var group = await catalog.GetModifierGroupAsync(option.ModifierGroupId);
        if (group is null) return;
        group.Options.RemoveAll(item => item.Id == option.Id);
        await catalog.SaveModifierGroupAsync(group);
        haptics.Click();
        await LoadAsync();
    }

    private async Task DeleteIngredientAsync(Ingredient? ingredient)
    {
        if (ingredient is null) return;
        var confirmed = await dialogs.ConfirmAsync(
            "Удалить ингредиент?",
            $"Ингредиент «{ingredient.Name}» будет удалён из склада и рецептов.",
            "Удалить");
        if (!confirmed) return;

        await catalog.DeleteIngredientAsync(ingredient.Id);
        haptics.Click();
        await LoadAsync();
    }

    private async Task ToggleIngredientAvailabilityAsync(Ingredient? ingredient)
    {
        if (ingredient is null) return;
        await catalog.ToggleIngredientAvailabilityAsync(ingredient.Id);
        await LoadAsync();
    }

    // ─── Row overflow menus ───
    // Each row opens the action sheet and dispatches the chosen key to the same code the inline
    // buttons used to call, so no behaviour was moved — only where it is triggered from.

    private async Task ShowProductActionsAsync(Product? product)
    {
        if (product is null) return;

        // Haptic confirmation for both the overflow-button tap and the row long press.
        haptics.Click();
        switch (await actionSheet.ChooseAsync(product.Name, CatalogActions.ForProduct(product)))
        {
            case CatalogActionKeys.Edit: RequestForm(CatalogFormKind.Product, product.Id); break;
            case CatalogActionKeys.Copy: await CopyProductAsync(product); break;
            case CatalogActionKeys.Delete: await DeleteProductAsync(product); break;
            case CatalogActionKeys.Restore: await RestoreProductAsync(product); break;
            case CatalogActionKeys.ToggleAvailability: await ToggleProductAvailabilityAsync(product); break;
        }
    }

    private async Task ShowCategoryActionsAsync(Category? category)
    {
        if (category is null) return;

        haptics.Click();
        switch (await actionSheet.ChooseAsync(category.Name, CatalogActions.ForCategory()))
        {
            case CatalogActionKeys.Edit: RequestForm(CatalogFormKind.Category, category.Id); break;
            case CatalogActionKeys.Delete: await DeleteCategoryAsync(category); break;
        }
    }

    private async Task ShowModifierGroupActionsAsync(ModifierOptionGroup? group)
    {
        if (group is null) return;

        haptics.Click();
        switch (await actionSheet.ChooseAsync(group.Name, CatalogActions.ForModifierGroup()))
        {
            case CatalogActionKeys.Edit: RequestForm(CatalogFormKind.ModifierGroup, group.GroupId); break;
            case CatalogActionKeys.Delete: await DeleteModifierGroupAsync(group); break;
        }
    }

    private async Task ShowModifierOptionActionsAsync(ModifierOption? option)
    {
        if (option is null) return;

        haptics.Click();
        switch (await actionSheet.ChooseAsync(option.Name, CatalogActions.ForModifierOption(option)))
        {
            case CatalogActionKeys.ToggleAvailability: await ToggleModifierOptionAvailabilityAsync(option); break;
            case CatalogActionKeys.RemoveFromGroup: await RemoveModifierOptionAsync(option); break;
        }
    }

    private async Task ShowIngredientActionsAsync(Ingredient? ingredient)
    {
        if (ingredient is null) return;

        haptics.Click();
        switch (await actionSheet.ChooseAsync(ingredient.Name, CatalogActions.ForIngredient(ingredient)))
        {
            case CatalogActionKeys.Edit: RequestForm(CatalogFormKind.Ingredient, ingredient.Id); break;
            case CatalogActionKeys.Delete: await DeleteIngredientAsync(ingredient); break;
            case CatalogActionKeys.ToggleAvailability: await ToggleIngredientAvailabilityAsync(ingredient); break;
        }
    }
}
