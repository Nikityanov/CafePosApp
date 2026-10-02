using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePosApp.Services;
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
            await LoadCoreAsync(clearMessage: true);
        }
        finally
        {
            IsBusy = false;
            loadGate.Release();
        }
    }

    /// <summary>
    /// Reloads the board after a change made here (status advance, payment, cancel). Unlike
    /// <see cref="LoadAsync"/> this waits for an in-flight poll instead of returning: a poll that
    /// was already in flight when the change happened would otherwise swallow the reload, the
    /// board would keep showing the pre-change state, and a payment just taken would read as
    /// "the sheet doesn't work" because the pictogram never updated.
    /// </summary>
    /// <remarks>
    /// This deliberately leaves <see cref="IsBusy"/> untouched. The RefreshView re-fires
    /// LoadCommand on a false→true transition of IsBusy, so a reload that waits for a poll and
    /// then queries does not re-trigger the spinner. The message is left for the caller to set.
    /// </remarks>
    public async Task ReloadAsync()
    {
        await loadGate.WaitAsync();
        try
        {
            await LoadCoreAsync(clearMessage: false);
        }
        finally
        {
            loadGate.Release();
        }
    }

    private async Task LoadCoreAsync(bool clearMessage)
    {
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

            // Each card's total is formatted in the operator's currency, which can be changed on the
            // Settings tab while this board is alive — Shell keeps one instance per tab, so returning
            // here re-runs this method rather than rebuilding the ViewModel. Re-raise so a board left
            // open across a switch stops printing the previous sign. See
            // MenuViewModel.RefreshMoneyText for the same argument on the cart.
            foreach (var row in ActiveOrders) row.RefreshMoneyText();

            if (clearMessage) Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load active orders");
            Message = UserMessages.Describe(exception, "Не удалось загрузить заказы");
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
            await ReloadAsync();
            Message = updated.Status == OrderStatus.Completed
                ? $"{row.OrderTitle} закрыт."
                : $"{row.OrderTitle} готов к выдаче.";
        }
        catch (ConflictException exception) when (IsUnpaidConflict(exception))
        {
            // The domain blocks advancing an unpaid order. Offer the payment sheet instead of a
            // plain error; if the operator pays, retry the advance. A dismissed sheet leaves the
            // order untouched and says nothing — the board already shows it as unpaid.
            if (await TryCollectPaymentAsync(row) is not PaymentOutcome.Settled) return;

            try
            {
                var updated = await orders.AdvanceStatusAsync(row.Model.Id);
                haptics.Click();
                await ReloadAsync();
                Message = updated.Status == OrderStatus.Completed
                    ? $"{row.OrderTitle} закрыт."
                    : $"{row.OrderTitle} готов к выдаче.";
            }
            catch (Exception retryException)
            {
                logger.LogError(retryException, "Failed to advance order {OrderId} after payment", row.Model.Id);
                Message = UserMessages.Describe(retryException, "Не удалось изменить статус заказа");
                haptics.Warn();
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to advance order {OrderId}", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось изменить статус заказа");
            haptics.Warn();
        }
    }

    /// <summary>What became of an attempt to collect payment for an order.</summary>
    /// <remarks>
    /// Three outcomes, not a bool. A bool could not tell "paid in full" from "partially paid", so a
    /// top-up still short of the total was reported the same way as a completed payment — the
    /// advance was then retried, failed the domain's own unpaid guard, and the operator was shown
    /// "Не удалось изменить статус заказа: Заказ не оплачен" immediately after handing over money.
    /// <see cref="StillOwing"/> exists so a partial payment states what is left instead.
    /// </remarks>
    private enum PaymentOutcome
    {
        /// <summary>The sheet was dismissed, or the payment itself failed.</summary>
        Dismissed,

        /// <summary>Money was taken but the order still has a balance.</summary>
        StillOwing,

        /// <summary>The order is settled, so the blocked transition may be retried.</summary>
        Settled
    }

    /// <summary>
    /// Opens the payment sheet for an order and books what the operator declares.
    /// </summary>
    /// <remarks>
    /// A <see cref="ConflictException"/> with "уже оплачен" is not an error: the board row is a
    /// snapshot from a poll up to <see cref="AppSettings.AutoRefreshSeconds"/> old, so the details
    /// screen may already have settled the order. It is treated as a reload at warning level, never
    /// as a failure.
    /// </remarks>
    private async Task<PaymentOutcome> TryCollectPaymentAsync(OrderRowViewModel row)
    {
        var payment = await paymentSheet.CollectAsync(new PaymentSheetRequest(
            row.OrderTitle,
            Money.FromKopecks(row.Model.BalanceKopecks),
            Money.FromKopecks(row.Model.PaidKopecks)));
        if (payment is null) return PaymentOutcome.Dismissed;

        try
        {
            var updated = await orders.AddPaymentAsync(row.Model.Id, payment.Amount, payment.Method);
            haptics.Click();
            await ReloadAsync();

            if (!updated.IsFullyPaid)
            {
                // A deposit, not a settlement. Say what is left and stop: retrying the transition
                // now would only bounce off the domain guard and report the payment as a failure.
                var balance = Money.FromKopecks(updated.BalanceKopecks);
                Message = $"{row.OrderTitle}: оплачено {TextFormat.Money(payment.Amount)} " +
                          $"({PaymentText.Method(payment.Method)}), осталось {TextFormat.Money(balance)}.";
                haptics.Warn();
                return PaymentOutcome.StillOwing;
            }

            Message = $"{row.OrderTitle}: оплачено {TextFormat.Money(payment.Amount)} ({PaymentText.Method(payment.Method)}).";
            return PaymentOutcome.Settled;
        }
        catch (ConflictException exception) when (IsAlreadyPaidConflict(exception))
        {
            logger.LogWarning("Order {OrderId} was already paid when the payment was applied; reloading", row.Model.Id);
            await ReloadAsync();
            return PaymentOutcome.Settled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to accept payment for order {OrderId}", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось принять оплату");
            haptics.Warn();
            return PaymentOutcome.Dismissed;
        }
    }

    private async Task CollectPaymentAsync(OrderRowViewModel? row)
    {
        if (row is null) return;
        await TryCollectPaymentAsync(row);
    }

    /// <summary>
    /// True when the conflict is the domain's "this order is unpaid" block on advancing. Matched
    /// on the message because the unpaid check lives in Core and the exact wording is Core's; the
    /// only other conflict from AdvanceStatusAsync is "Этот заказ уже закрыт.", which does not
    /// contain "оплат".
    /// </summary>
    private static bool IsUnpaidConflict(ConflictException exception) =>
        exception.Message.Contains("оплат", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the conflict is "the order is already paid" from a payment that raced a poll.
    /// </summary>
    private static bool IsAlreadyPaidConflict(ConflictException exception) =>
        exception.Message.Contains("уже оплачен", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The whole cancellation, in the order the operator has to make the decisions: confirm,
    /// disposition, reason. Then one call that voids the sale, refunds it and settles the stock.
    /// </summary>
    /// <remarks>
    /// The stock disposition sits between the confirmation and the reason, not after both. It is
    /// the only one of the three whose answer changes the shelf rather than the books, so it is
    /// the one that must not be skippable and must not be last — after the reason prompt the
    /// operator has already typed their way out of the screen. It is also asked BEFORE the reason
    /// because a cancellation reason written for "блюдо отменили" and one written for "остатки
    /// вернули" are different sentences, and the disposition is what tells the operator which kind
    /// of thing they are describing.
    /// <para>
    /// Every step is a separate dialog and every one is a way out. The disposition sheet returns
    /// null when dismissed, and a dismissal is treated exactly like tapping "Назад" at the
    /// confirmation: nothing happens, which is the only safe reading of "the operator changed
    /// their mind".
    /// </para>
    /// </remarks>
    private async Task CancelOrderAsync(OrderRowViewModel? row)
    {
        if (row is null) return;

        try
        {
            // The confirmation names the money when money is involved. "Будет отменён" on a paid
            // order is now a lie by omission: cancelling takes the payment with it.
            var paid = Money.FromKopecks(row.Model.PaidKopecks);
            var summary = row.Model.PaidKopecks > 0
                ? $"{row.OrderTitle} на {TextFormat.Money(row.TotalPrice)} будет отменён, клиенту вернётся {TextFormat.Money(paid)}."
                : $"{row.OrderTitle} на {TextFormat.Money(row.TotalPrice)} будет отменён.";

            if (!await dialogs.ConfirmAsync("Отменить заказ?", summary, row.CancelText, "Назад"))
            {
                return;
            }

            var stock = await stockDisposition.ChooseAsync(new StockDispositionSheetRequest(
                row.OrderTitle,
                row.TotalPrice,
                paid));
            if (stock is null) return;

            var reason = await dialogs.PromptAsync("Причина отмены", "Необязательно: причина отмены заказа", string.Empty);
            await orders.CancelOrderAsync(row.Model.Id, reason, stock.Value);
            await ReloadAsync();

            // The money is named in the outcome, not just in the confirmation: this is the line the
            // operator reads afterwards to check they moved the right amount.
            Message = row.Model.PaidKopecks > 0
                ? $"{row.OrderTitle} отменён, возвращено {TextFormat.Money(paid)}."
                : $"{row.OrderTitle} отменён.";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to cancel order {OrderId}", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось отменить заказ");
            haptics.Warn();
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

