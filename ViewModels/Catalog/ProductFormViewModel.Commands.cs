using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Load, reset and save behaviour of the product card form.</summary>
public partial class ProductFormViewModel
{
    /// <summary>Text of the "no category" picker row.</summary>
    private const string NoCategoryText = "— Без раздела —";

    /// <summary>Text of the "no modifier group" picker row.</summary>
    private const string NoModifierGroupText = "— Без группы модификаторов —";

    public async Task LoadAsync(Guid? productId)
    {
        Reset();
        editingProductId = productId;

        CategoryOptions.SyncWith(
            (await catalog.GetCategoriesAsync()).WithNone(NoCategoryText, category => category.Name),
            option => option.Value?.Id ?? Guid.Empty);
        ModifierGroupOptions.SyncWith(
            (await catalog.GetModifierGroupsAsync()).WithNone(NoModifierGroupText, group => group.Name),
            option => option.Value?.Id ?? Guid.Empty);
        Ingredients.SyncWith(await catalog.GetIngredientsAsync(), ingredient => ingredient.Id);

        // Default both optional links to their "none" row, so the pickers show an explicit
        // state instead of a blank title.
        SelectedCategoryOption = NoneRow(CategoryOptions);
        SelectedModifierGroupOption = NoneRow(ModifierGroupOptions);

        if (productId is not null)
        {
            var product = await catalog.GetProductAsync(productId.Value);
            if (product is null)
            {
                ValidationMessage = "Товар не найден.";
                return;
            }

            ProductName = product.Name;
            ProductPriceText = product.Price.ToString("0.##");
            // Re-point the selection at the freshly synced rows, otherwise the picker would
            // hold an item that is no longer in ItemsSource.
            SelectedCategoryOption = SelectRow(CategoryOptions, product.CategoryId, category => category.Id);
            SelectedModifierGroupOption = SelectRow(ModifierGroupOptions, product.ModifierGroupId, group => group.Id);
            HasVariants = product.HasVariants;
            Variants.SyncWith(product.Variants.OrderBy(variant => variant.SortOrder), variant => variant.Id);
            PhotoPath = product.PhotoPath ?? string.Empty;
            Allergens = product.Allergens ?? string.Empty;
            Tags = product.Tags ?? string.Empty;
            FromHourIndex = product.AvailableFromHour is { } from ? from + 1 : 0;
            ToHourIndex = product.AvailableToHour is { } to ? to + 1 : 0;

            Recipes.SyncWith(await catalog.GetRecipeItemsByProductAsync(product.Id), item => item.Id);
            await RefreshComputedAsync();
        }

        NotifyFormState();
        NotifyEmptyStates();
        logger.LogDebug("Product form loaded (editing: {IsEditing})", IsEditing);
    }

    /// <summary>The "none" row of a picker, i.e. no link at all.</summary>
    private static CatalogOption<T>? NoneRow<T>(ObservableCollection<CatalogOption<T>> options) where T : class =>
        options.FirstOrDefault(option => option.IsNone);

    /// <summary>Row to preselect for an optional link: the matching row, or the "none" row.</summary>
    private static CatalogOption<T>? SelectRow<T>(ObservableCollection<CatalogOption<T>> options, Guid? id, Func<T, Guid> idSelector)
        where T : class =>
        (id is null ? null : options.FirstOrDefault(option => option.Value is { } value && idSelector(value) == id))
        ?? NoneRow(options);

    public void Reset()
    {
        editingProductId = null;
        costPrice = 0;
        ProductName = string.Empty;
        ProductPriceText = string.Empty;
        SelectedCategoryOption = null;
        SelectedModifierGroupOption = null;
        HasVariants = false;
        Variants.Clear();
        NewVariantName = string.Empty;
        NewVariantPriceText = string.Empty;
        PhotoPath = string.Empty;
        Allergens = string.Empty;
        Tags = string.Empty;
        FromHourIndex = 0;
        ToHourIndex = 0;
        Recipes.Clear();
        PriceHistory.Clear();
        SelectedRecipeIngredient = null;
        RecipeQuantityText = string.Empty;
        ValidationMessage = string.Empty;
        NotifyFormState();
        NotifyComputed();
        NotifyEmptyStates();
    }

    private async Task SaveAsync()
    {
        ValidationMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(ProductName))
        {
            ValidationMessage = "Укажите название товара.";
            return;
        }

        decimal price;
        if (HasVariants)
        {
            if (Variants.Count == 0)
            {
                ValidationMessage = "Добавьте хотя бы один вариант или снимите флажок «Есть варианты».";
                return;
            }

            if (!Variants.Any(variant => variant.IsAvailable))
            {
                ValidationMessage = "Все варианты сняты с продажи: блюдо нельзя будет добавить в корзину. Снимите флажок «Есть варианты» или включите хотя бы один вариант.";
                return;
            }

            var duplicateName = Variants
                .Select(variant => variant.Name.Trim())
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateName is not null)
            {
                ValidationMessage = $"Название варианта «{duplicateName.Key}» повторяется.";
                return;
            }

            // Each variant carries its own price, so the product itself is priced at zero.
            price = 0;
        }
        else if (!TextFormat.TryParseDecimal(ProductPriceText, out price) || price < 0)
        {
            ValidationMessage = "Введите корректную цену.";
            return;
        }

        // Both ends of the availability window are optional (index 0 = "Не задано"), so only
        // reject an inverted window when the operator actually set both. A 23:00→09:00 range
        // used to save silently, and the product then never appeared on the menu.
        if (FromHourIndex > 0 && ToHourIndex > 0 && FromHourIndex >= ToHourIndex)
        {
            ValidationMessage = "Час «Доступен с» должен быть раньше часа «Доступен до».";
            return;
        }

        IsBusy = true;
        try
        {
            var product = new Product
            {
                Id = editingProductId ?? Guid.NewGuid(),
                Name = ProductName.Trim(),
                Price = price,
                IsAvailable = true,
                CategoryId = SelectedCategory?.Id,
                ModifierGroupId = SelectedModifierGroup?.Id,
                HasVariants = HasVariants,
                PhotoPath = string.IsNullOrWhiteSpace(PhotoPath) ? null : PhotoPath,
                Allergens = string.IsNullOrWhiteSpace(Allergens) ? null : Allergens.Trim(),
                Tags = string.IsNullOrWhiteSpace(Tags) ? null : Tags.Trim(),
                AvailableFromHour = FromHourIndex > 0 ? FromHourIndex - 1 : null,
                AvailableToHour = ToHourIndex > 0 ? ToHourIndex - 1 : null,
                Variants = [.. Variants.Select(variant => new ProductVariant
                {
                    Id = variant.Id,
                    Name = variant.Name,
                    Price = variant.Price,
                    IsAvailable = variant.IsAvailable,
                    SortOrder = variant.SortOrder
                })]
            };

            if (editingProductId is not null)
            {
                var existing = await catalog.GetProductAsync(editingProductId.Value);
                product.IsAvailable = existing?.IsAvailable ?? true;
            }

            await catalog.SaveProductAsync(product);

            if (editingProductId is null)
            {
                editingProductId = product.Id;
            }

            Saved?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the product");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить товар");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
