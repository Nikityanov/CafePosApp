using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Extensions.Logging;

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

    // ─── Bulk pricing / CSV ───

    /// <summary>
    /// The single "CSV" button. MAUI's Button has no Flyout property, so the two round-trips go
    /// through the same action sheet the row overflow uses instead of a MenuFlyout.
    /// </summary>
    private async Task ShowCsvActionsAsync()
    {
        haptics.Click();
        switch (await actionSheet.ChooseAsync("Каталог в формате CSV", CatalogActions.ForCsv()))
        {
            case CatalogActionKeys.ExportCsv: await ExportCsvAsync(); break;
            case CatalogActionKeys.ImportCsv: await ImportCsvAsync(); break;
        }
    }

    private async Task BulkPriceAdjustAsync()
    {
        if (!TextFormat.TryParseDecimal(BulkPricePercentText, out var percent))
        {
            SetNotice("Введите корректный процент (например, +10 или -5).", NoticeLevel.Error);
            return;
        }

        // The target is the ticked rows, or the whole filtered list when nothing is ticked.
        // The confirmation names it either way — this writes a price history entry per product
        // and is not something to fire blind.
        var target = HasSelection
            ? SelectedProducts.ToList()
            : FilteredProducts.ToList();

        if (target.Count == 0)
        {
            SetNotice("Нет товаров для корректировки.", NoticeLevel.Error);
            return;
        }

        var scope = HasSelection
            ? $"выбранных товаров ({target.Count})"
            : "всех товаров в списке";
        var confirmed = await dialogs.ConfirmAsync(
            "Массовая корректировка цен",
            $"Изменить цены {scope} на {percent:+0.##;-0.##}%? Действие попадёт в историю цен.",
            "Применить");
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var adjusted = 0;
            foreach (var product in target)
            {
                var oldPrice = product.Price;
                var newPrice = Money.ApplyPercent(oldPrice, percent);
                if (newPrice < 0 || newPrice == oldPrice) continue;

                product.Price = newPrice;
                await catalog.SaveProductAsync(product);
                await catalog.LogPriceChangeAsync(product.Id, oldPrice, newPrice, $"Массовая корректировка {percent:+0.##;-0.##}%");
                adjusted++;
            }

            SetNotice($"Скорректировано {adjusted} {Plural(adjusted, "товар", "товара", "товаров")} на {percent:+0.##;-0.##}%", NoticeLevel.Success);
            BulkPricePercentText = string.Empty;
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to adjust prices across the catalogue");
            SetNotice(UserMessages.Describe(exception, "Не удалось скорректировать цены"), NoticeLevel.Error);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    private async Task ExportCsvAsync()
    {
        try
        {
            var csv = await catalog.ExportProductsCsvAsync();
            var path = await files.SaveTextReportAsync($"catalog_{DateTime.Now:yyyyMMdd_HHmm}.csv", csv);
            await files.ShareFileAsync(path, "Каталог товаров");
            logger.LogInformation("Catalogue exported to {Path}", path);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export the catalogue to CSV");
            SetNotice(UserMessages.Describe(exception, "Не удалось экспортировать каталог"), NoticeLevel.Error);
        }
    }

    private async Task ImportCsvAsync()
    {
        try
        {
            var content = await files.PickCsvTextAsync();
            if (content is null) return;

            var result = await catalog.ImportProductsCsvAsync(content);
            var hasWarnings = result.Warnings.Count > 0;
            SetNotice(
                hasWarnings ? $"{result.Summary}. Предупреждений: {result.Warnings.Count}." : result.Summary,
                hasWarnings ? NoticeLevel.Error : NoticeLevel.Success);
            haptics.Click();
            await LoadAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import the catalogue from CSV");
            SetNotice(UserMessages.Describe(exception, "Не удалось импортировать каталог"), NoticeLevel.Error);
        }
    }
}
