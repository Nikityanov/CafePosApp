using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Order board behaviour.</summary>
public partial class OrdersViewModel
{
    public async Task LoadAsync()
    {
        // Re-entrancy guard. RefreshView.IsRefreshing is bound to IsBusy; when LoadAsync
        // sets IsBusy = true the RefreshView fires LoadCommand, which calls LoadAsync again.
        // Without this guard the second call blocks on loadGate, and when the first finishes
        // the second one sets IsBusy = true again — a feedback loop that re-executes the
        // Orders query on every cycle (infinite loading). The ShiftReportViewModel uses the
        // same guard. AllowConcurrentExecutions is kept so the AsyncRelayCommand always
        // starts a task the RefreshView can track (otherwise the spinner can get stuck).
        if (IsBusy) return;

        await loadGate.WaitAsync();
        IsBusy = true;
        try
        {
            var activeOrders = await orders.GetActiveOrdersAsync();
            // Preparing ahead of ready, then oldest first. Ascending on (Status == Ready)
            // puts false — preparing — first and true — ready for pickup — last, which is the
            // order the board is read in. Descending here was briefly wrong and put
            // "Ждут выдачи" above "Готовятся", which is the opposite of what was asked for.
            // LoadAsync runs again after every status change, so the list re-sorts itself
            // when an order is advanced rather than waiting for a manual refresh.
            var rows = activeOrders
                .OrderBy(order => order.Status == OrderStatus.Ready)
                .ThenBy(order => order.OrderNumber)
                .Select(order => new OrderRowViewModel(order, settings))
                .ToList();

            ActiveOrders.SyncWith(rows, row => row.Model.Id);

            // The first card of each section carries the section name. Done after SyncWith,
            // which reuses OrderRowViewModel instances across refreshes, so the flags have to
            // be recomputed every load — a row kept from the previous pass still holds the old
            // value, and a section that became empty would keep its header.
            string? previousGroup = null;
            foreach (var row in rows)
            {
                row.ShowGroupHeader = row.StatusGroupName != previousGroup;
                previousGroup = row.StatusGroupName;
            }
            foreach (var row in ActiveOrders.Where(row => !rows.Contains(row)))
                row.ShowGroupHeader = false;

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load active orders");
            Message = UserMessages.Describe(exception, "Не удалось загрузить заказы");
        }
        finally
        {
            IsBusy = false;
            loadGate.Release();
        }
    }

    public void StartAutoRefresh()
    {
        if (refreshTask is not null) return;
        refreshCancellation = new CancellationTokenSource();
        refreshTask = RefreshLoopAsync(refreshCancellation.Token);
    }

    public void StopAutoRefresh()
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
        refreshTask = null;
    }

    private async Task AdvanceStatusAsync(OrderRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            var updated = await orders.AdvanceStatusAsync(row.Model.Id);
            haptics.Click();
            await LoadAsync();
            Message = updated.Status == OrderStatus.Completed
                ? $"{row.OrderTitle} закрыт."
                : $"{row.OrderTitle} готов к выдаче.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to advance order {OrderId}", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось изменить статус заказа");
            haptics.Warn();
        }
    }

    /// <summary>Cancels an order, asking for an optional reason (previously the reason was always null).</summary>
    private async Task CancelOrderAsync(OrderRowViewModel? row)
    {
        if (row is null) return;

        try
        {
            if (!await dialogs.ConfirmAsync("Отменить заказ?", $"{row.OrderTitle} на {TextFormat.Money(row.TotalPrice)} будет отменён.", "Отменить заказ", "Назад"))
            {
                return;
            }

            var reason = await dialogs.PromptAsync("Причина отмены", "Необязательно: причина отмены заказа", string.Empty);
            await orders.CancelOrderAsync(row.Model.Id, reason);
            await LoadAsync();
            Message = $"{row.OrderTitle} отменён.";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to cancel order {OrderId}", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось отменить заказ");
        }
    }

    private async Task OpenDetailsAsync(OrderRowViewModel? row)
    {
        if (row is null) return;
        await navigation.GoToOrderDetailsAsync(row.Model.Id);
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // PeriodicTimer throws on a non-positive period, and the interval is stored in
            // settings, so it is clamped here instead of trusting the saved value.
            var seconds = Math.Clamp(settings.AutoRefreshSeconds, 5, 300);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await LoadAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the page disappears.
        }
    }
}

