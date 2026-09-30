using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace CafePosApp.ViewModels;

/// <summary>Behaviour of the menu / cart screen.</summary>
public partial class MenuViewModel
{
    private void SelectCategory(CategoryMenuItemViewModel? item)
    {
        SelectedCategory = item?.Category;
        HighlightSelectedChip();
        ApplyFilters();
    }

    /// <summary>Keeps the chip strip in sync with <see cref="SelectedCategory"/>.</summary>
    private void HighlightSelectedChip()
    {
        foreach (var chip in Categories)
            chip.IsSelected = chip.Category?.Id == SelectedCategory?.Id
                || (chip.IsAll && SelectedCategory is null);
    }

    /// <summary>
    /// Runs the filter straight away. Every keystroke used to re-scan every product and re-diff
    /// the whole list, which stutters on a long menu, so the search box goes through
    /// <see cref="ScheduleFilterRefresh"/> instead.
    /// </summary>
    private void ApplyFilters()
    {
        var localHour = timeProvider.GetLocalNow().Hour;
        var query = Products.Where(product =>
            (SelectedCategory is null || product.CategoryId == SelectedCategory.Id)
            && (string.IsNullOrWhiteSpace(SearchText) || product.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            && ProductAvailability.IsInTimeWindow(product.AvailableFromHour, product.AvailableToHour, localHour));

        // Diff based filtering: no Clear(), so the list does not flicker or lose its scroll.
        FilteredProducts.SyncWith(query, product => product.Id);
    }

    /// <summary>
    /// Restarts the debounce timer on every keystroke, so the filter runs once typing pauses.
    /// A section switch still calls <see cref="ApplyFilters"/> directly.
    /// </summary>
    private void ScheduleFilterRefresh()
    {
        // Cancel the pending run. Not disposing the source is deliberate: Task.Delay registers a
        // timer rather than a wait handle, so there is no OS resource to release.
        Interlocked.Exchange(ref filterCancellation, new CancellationTokenSource())?.Cancel();

        var token = filterCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounce, token);
                if (token.IsCancellationRequested) return;

                // The timer thread must not touch the bound collections.
                MainThread.BeginInvokeOnMainThread(ApplyFilters);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer keystroke — the newest one does the work.
            }
        }, token);
    }

    public async Task LoadAsync()
    {
        await loadGate.WaitAsync();
        IsBusy = true;
        try
        {
            var products = await catalog.GetProductsAsync();
            var categories = await catalog.GetCategoriesAsync();

            Products.SyncWith(products, product => product.Id);

            // The "Все" chip is the first element of the strip, so the whole row scrolls as one list.
            Categories.SyncWith(
                new[] { CategoryMenuItemViewModel.CreateAll() }
                    .Concat(categories.Select((category, index) => new CategoryMenuItemViewModel(category, index))),
                item => item.Key);

            // The selected section may have been deleted while the page was closed.
            if (SelectedCategory is not null && Categories.All(item => item.Category?.Id != SelectedCategory.Id))
            {
                SelectedCategory = null;
            }

            HighlightSelectedChip();
            ApplyFilters();
            await RestoreDraftAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the menu");
            SetError(exception, "Не удалось загрузить меню");
        }
        finally
        {
            IsBusy = false;
            loadGate.Release();
        }
    }

    private async Task RestoreDraftAsync()
    {
        if (Cart.Count > 0) return;

        var snapshot = await drafts.LoadActiveCartAsync();
        if (snapshot.IsEmpty) return;

        foreach (var line in snapshot.Lines) Cart.Add(CartItemViewModel.FromLine(line));
        Recalculate();
        Message = snapshot.SavedAt is { } savedAt
            ? $"Восстановлен несохранённый чек от {savedAt.ToLocalTime():HH:mm}"
            : "Восстановлен несохранённый чек";
    }

    private async Task AddProductAsync(Product? product)
    {
        if (product is null) return;
        if (!product.IsAvailable)
        {
            Message = "Это блюдо закончилось.";
            haptics.Warn();
            return;
        }

        try
        {
            // Which sheets open, when a cancel kills the add and which price applies are all
            // ProductAddFlow decisions. Only the two awaits and the cart mutation stay here.
            var flow = ProductAddFlow.Start(product);

            var modifierGroup = flow.ModifierGroup;
            var modifier = modifierGroup is null ? null : await modifierPicker.PickAsync(modifierGroup);
            if (flow.SubmitModifier(modifier) == AddToCartStepOutcome.Dismissed) return;

            string? variant = null;
            if (flow.RequiresVariantChoice)
            {
                // Every variant is sold out: there is nothing to show and no price to charge, so
                // the product cannot be added at all. Unlike a dismiss, the user is told why.
                if (!flow.CanOfferVariantChoice)
                {
                    Message = ProductAddFlow.NoAvailableVariantsMessage;
                    haptics.Warn();
                    return;
                }

                variant = await variantPicker.PickAsync(product.Name, flow.AvailableVariants);
                if (flow.SubmitVariant(variant) == AddToCartStepOutcome.Dismissed) return;
            }

            // A selected variant carries its own price and replaces the product price entirely.
            var effectivePrice = flow.ResolvePrice(variant);

            var existing = Cart.FirstOrDefault(item =>
                item.ProductId == product.Id
                && item.SelectedModifierName == modifier
                && item.SelectedVariantName == variant);

            if (existing is null)
            {
                Cart.Add(new CartItemViewModel
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Price = effectivePrice,
                    SelectedModifierName = modifier,
                    SelectedVariantName = variant,
                    Quantity = 1
                });
            }
            else
            {
                existing.Quantity++;
            }

            Recalculate();
            Message = string.Empty;
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to add product {ProductId} to the cart", product.Id);
            SetError(exception, "Не удалось добавить блюдо");
        }
    }

    private void AddItem(CartItemViewModel? item)
    {
        if (item is null) return;
        item.Quantity++;
        Recalculate();
    }

    private void RemoveItem(CartItemViewModel? item)
    {
        if (item is null) return;
        item.Quantity--;
        if (item.Quantity <= 0) Cart.Remove(item);
        Recalculate();
    }

    private void Recalculate() => Total = Cart.Sum(item => item.LineTotal);
}
