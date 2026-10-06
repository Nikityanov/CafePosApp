using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Variants, recipe, photo and computed values of the product form.</summary>
public partial class ProductFormViewModel
{
    private void AddVariant()
    {
        ValidationMessage = string.Empty;
        var name = NewVariantName.Trim();
        if (name.Length == 0) { ValidationMessage = "Укажите название варианта."; return; }
        if (!TextFormat.TryParseDecimal(NewVariantPriceText, out var price) || price < 0) { ValidationMessage = "Введите корректную цену варианта."; return; }
        if (Variants.Any(variant => string.Equals(variant.Name, name, StringComparison.OrdinalIgnoreCase))) { ValidationMessage = "Вариант с таким названием уже добавлен."; return; }

        Variants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            Name = name,
            Price = price,
            IsAvailable = true,
            SortOrder = Variants.Count
        });
        NewVariantName = string.Empty;
        NewVariantPriceText = string.Empty;
        NotifyEmptyStates();
        NotifyMargins();
    }

    private void RemoveVariant(ProductVariant? variant)
    {
        if (variant is null) return;
        Variants.Remove(variant);
        NotifyEmptyStates();
        NotifyMargins();
    }

    private async Task AddRecipeItemAsync()
    {
        ValidationMessage = string.Empty;
        if (editingProductId is null) { ValidationMessage = "Сначала сохраните товар и откройте его снова."; return; }
        if (SelectedRecipeIngredient is null) { ValidationMessage = "Выберите ингредиент."; return; }
        if (!TextFormat.TryParseDecimal(RecipeQuantityText, out var quantity) || quantity <= 0) { ValidationMessage = "Введите количество больше нуля."; return; }

        try
        {
            var existing = Recipes.FirstOrDefault(item => item.IngredientId == SelectedRecipeIngredient.Id);
            if (existing is not null)
            {
                existing.Quantity = quantity;
                await catalog.SaveRecipeItemAsync(existing);
            }
            else
            {
                var recipeItem = new RecipeItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = editingProductId.Value,
                    IngredientId = SelectedRecipeIngredient.Id,
                    Ingredient = SelectedRecipeIngredient,
                    Quantity = quantity
                };
                await catalog.SaveRecipeItemAsync(recipeItem);
                Recipes.Add(recipeItem);
            }

            SelectedRecipeIngredient = null;
            RecipeQuantityText = string.Empty;
            NotifyEmptyStates();
            await RefreshComputedAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to save the recipe item");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось сохранить ингредиент рецепта");
        }
    }

    private async Task DeleteRecipeItemAsync(RecipeItem? recipeItem)
    {
        if (recipeItem is null) return;
        await catalog.DeleteRecipeItemAsync(recipeItem.Id);
        Recipes.Remove(recipeItem);
        NotifyEmptyStates();
        await RefreshComputedAsync();
    }

    private async Task PickPhotoAsync()
    {
        try
        {
            var path = await files.PickImageAsync("Выберите фото товара");
            if (path is not null) PhotoPath = path;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to pick the product photo");
            ValidationMessage = UserMessages.Describe(exception, "Не удалось выбрать фото");
        }
    }

    private async Task RefreshComputedAsync()
    {
        if (editingProductId is null)
        {
            costPrice = 0;
            PriceHistory.Clear();
            NotifyComputed();
            NotifyEmptyStates();
            return;
        }

        costPrice = await catalog.CalculateCostPriceAsync(editingProductId.Value);
        var history = await catalog.GetPriceHistoryAsync(editingProductId.Value, 5);
        PriceHistory.SyncWith(history, entry => entry.Id);
        NotifyComputed();
        NotifyEmptyStates();
    }

    private void NotifyFormState()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveText));
        NotifyMargins();
    }
}
