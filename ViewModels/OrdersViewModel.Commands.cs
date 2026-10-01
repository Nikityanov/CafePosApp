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
            SyncSectionFilters();
            ApplyFilter();

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

    /// <summary>
    /// Brings the filter chips in line with the loaded orders: the counts, and which one is active.
    /// </summary>
    /// <remarks>
    /// The chips are built once and then updated, not recreated on every load. Recreating them
    /// would drop the selection highlight on every auto-refresh tick, which is several times a
    /// minute while the operator is trying to read a count.
    /// </remarks>
    private void SyncSectionFilters()
    {
        if (SectionFilters.Count == 0)
        {
            SectionFilters.Add(new OrderFilterChip(OrderSectionFilter.All, "Все"));
            SectionFilters.Add(new OrderFilterChip(OrderSectionFilter.Preparing, "Готовятся"));
            SectionFilters.Add(new OrderFilterChip(OrderSectionFilter.Ready, "Ждут выдачи"));
            foreach (var chip in SectionFilters)
            {
                chip.IsSelected = chip.Filter == ActiveFilter;
            }
        }

        var preparing = 0;
        foreach (var row in ActiveOrders)
        {
            if (row.Model.Status != OrderStatus.Ready) preparing++;
        }

        foreach (var chip in SectionFilters)
        {
            chip.Count = chip.Filter switch
            {
                OrderSectionFilter.Preparing => preparing,
                OrderSectionFilter.Ready => ActiveOrders.Count - preparing,
                _ => ActiveOrders.Count
            };
        }
    }

    /// <summary>
    /// Narrows <see cref="ActiveOrders"/> down to <see cref="VisibleOrders"/> per
    /// <see cref="ActiveFilter"/>, and decides which cards carry their section heading.
    /// </summary>
    /// <remarks>
    /// The heading is suppressed when a single section is selected, because the chip the operator
    /// just tapped already says which section they are looking at; repeating it on the first card
    /// would be the same words twice. It is also recomputed on every pass rather than once, since
    /// SyncWith reuses row instances across refreshes and a reused row still holds the previous
    /// pass's flag — that is what would otherwise leave a stale heading above the second card of a
    /// section, or keep the heading of a section that has just emptied.
    /// </remarks>
    private void ApplyFilter()
    {
        var showHeadings = ActiveFilter == OrderSectionFilter.All;
        var visible = new List<OrderRowViewModel>();
        string? previousGroup = null;

        foreach (var row in ActiveOrders)
        {
            if (!Matches(row))
            {
                row.ShowGroupHeader = false;
                continue;
            }

            row.ShowGroupHeader = showHeadings && row.StatusGroupName != previousGroup;
            previousGroup = row.StatusGroupName;
            visible.Add(row);
        }

        VisibleOrders.SyncWith(visible, row => row.Model.Id);
        OnPropertyChanged(nameof(EmptyListText));
    }

    private bool Matches(OrderRowViewModel row) => ActiveFilter switch
    {
        OrderSectionFilter.Preparing => row.Model.Status != OrderStatus.Ready,
        OrderSectionFilter.Ready => row.Model.Status == OrderStatus.Ready,
        _ => true
    };

    /// <summary>
    /// Switches the board between both sections and one of them. Re-projects what is already
    /// loaded rather than querying again: the rows for the other section are still in
    /// <see cref="ActiveOrders"/>, so the switch is immediate and the next auto-refresh tick
    /// agrees with it.
    /// </summary>
    private void SelectSectionFilter(OrderFilterChip? chip)
    {
        if (chip is null || chip.Filter == ActiveFilter) return;

        ActiveFilter = chip.Filter;
        foreach (var candidate in SectionFilters)
        {
            candidate.IsSelected = candidate.Filter == ActiveFilter;
        }

        ApplyFilter();
        haptics.Click();
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

