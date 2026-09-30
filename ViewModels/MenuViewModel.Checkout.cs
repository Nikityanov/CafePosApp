using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Checkout, cart autosave and parked ("held") carts.</summary>
public partial class MenuViewModel
{
    private async Task CreateOrderAsync()
    {
        if (Cart.Count == 0)
        {
            Message = "Добавьте товары в заказ.";
            return;
        }

        IsBusy = true;
        try
        {
            var lines = Cart.Select(item => item.ToCheckoutLine()).ToList();

            // Pre-flight check so the operator gets an actionable message before the transaction.
            var shortages = await inventory.PreviewShortagesAsync(lines.Select(line => (line.ProductId, line.Quantity)).ToList());
            if (shortages.Count > 0)
            {
                throw new InsufficientStockException(shortages
                    .Select(shortage => $"{shortage.IngredientName}: нужно {shortage.Required:0.##} {shortage.Unit}, есть {shortage.Available:0.##} {shortage.Unit}")
                    .ToList());
            }

            var order = await checkout.CheckoutAsync(lines);

            Cart.Clear();
            Recalculate();
            await drafts.ClearActiveCartAsync();
            Message = $"Заказ #{order.OrderNumber} создан на {TextFormat.Money(order.TotalPrice)}.";
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Checkout failed");
            Message = UserMessages.Describe(exception, "Не удалось создать заказ");
            haptics.Warn();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ParkOrderAsync()
    {
        if (Cart.Count == 0)
        {
            Message = "Чек пуст.";
            return;
        }

        try
        {
            var suggestedName = $"Чек {timeProvider.GetLocalNow():HH:mm}";
            var name = await dialogs.PromptAsync("Отложить чек", "Название отложенного чека", suggestedName);
            if (name is null) return;

            var parked = await drafts.ParkAsync(name, Cart.Select(item => item.ToCheckoutLine()).ToList());
            Cart.Clear();
            Recalculate();
            await drafts.ClearActiveCartAsync();
            Message = $"Чек отложен: {parked.Name}.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to park the cart");
            Message = UserMessages.Describe(exception, "Не удалось отложить чек");
        }
    }

    private async Task OpenParkedAsync()
    {
        try
        {
            var parked = await drafts.GetParkedAsync();
            if (parked.Count == 0)
            {
                Message = "Отложенных чеков нет.";
                return;
            }

            if (Cart.Count > 0 && !await dialogs.ConfirmAsync("Отложенные чеки", "Текущий чек будет очищен и заменён отложенным. Продолжить?")) return;

            var draftId = await draftPicker.PickAsync(parked);
            if (draftId is null) return;

            var snapshot = await drafts.TakeAsync(draftId.Value);
            Cart.Clear();
            foreach (var line in snapshot.Lines) Cart.Add(CartItemViewModel.FromLine(line));
            Recalculate();
            Message = "Отложенный чек загружен.";
            haptics.Click();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to open a parked cart");
            Message = UserMessages.Describe(exception, "Не удалось открыть отложенный чек");
        }
    }

    /// <summary>
    /// Debounced autosave. The lines are snapshotted on the calling (UI) thread, the write itself
    /// happens after a short delay so typing/stepping does not hit the database on every tap.
    /// </summary>
    private void ScheduleAutoSave()
    {
        // Cancel but do NOT dispose. The superseded AutoSaveAsync still holds this token and
        // may be inside drafts.SaveActiveCartAsync right now; disposing a CancellationTokenSource
        // whose token is in use is a race of its own. The superseded run disposes its own source
        // when it finishes.
        autoSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        autoSaveCancellation = cancellation;

        var lines = Cart.Select(item => item.ToCheckoutLine()).ToList();
        _ = AutoSaveAsync(lines, cancellation);
    }

    private async Task AutoSaveAsync(IReadOnlyList<CheckoutLine> lines, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(AutoSaveDelay, cancellation.Token);

            // The gate is taken AFTER the debounce and released in finally, so an overlapping
            // snapshot waits for the in-flight write instead of racing it. The superseded
            // snapshot is already cancelled by then and its SaveChanges is skipped, so the
            // waiter writes the newer lines and the last write still wins.
            await autoSaveGate.WaitAsync(cancellation.Token);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await drafts.SaveActiveCartAsync(lines, cancellation.Token);
            }
            finally
            {
                autoSaveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change superseded this snapshot.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cart autosave failed");
        }
        finally
        {
            // Only the source this run owns, and only if it has not already been replaced.
            if (ReferenceEquals(autoSaveCancellation, cancellation)) autoSaveCancellation = null;
            cancellation.Dispose();
        }
    }
}
