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

            // ONE instant for the whole pass. Every section decision, every lateness figure and every
            // sort key on this board is measured against it, so two orders promised for the same
            // minute cannot land on opposite sides of a boundary because the clock ticked between
            // their rows. The rows are built with it in hand rather than reading a clock themselves.
            var now = timeProvider.GetUtcNow();

            // Three sections, and the section is the PRIMARY sort key: «Срочные» above «В работе»
            // above «По времени», because that is the order a kitchen should read them in.
            //
            // Inside a section the order is by PromisedAt and then CreatedAt. PromisedAt is the promise
            // — what the customer asked for, or CreatedAt + lead time — so the queue is sorted by what is
            // owed rather than by when the order happened to be typed; CreatedAt breaks the tie so two
            // orders promised for the same minute stay in the order they were taken.
            //
            // It used to sort preparing ahead of ready and then by order number, which was right when
            // the board's question was "which status has this order reached" and is not the question
            // now. Note what the sort deliberately does NOT do: it does not promote a late order above
            // the others within «В работе», because lateness is a STATE and not a sort key — see
            // OrderRowViewModel. An overdue order carries its badge wherever the promise puts it, and
            // «Срочные» is a section, not a rule that reorders the other two.
            // Three buckets, built in ONE pass so every card is judged against the same `now` — the
            // reason the loader takes the clock once and hands it to each row. Three separate queries
            // would put three different instants behind the three tabs, and a pre-order could be
            // "scheduled" on one tab and "being made" on another.
            var preparing = new List<OrderRowViewModel>();
            var ready = new List<OrderRowViewModel>();
            var scheduled = new List<OrderRowViewModel>();

            foreach (var order in activeOrders)
            {
                var row = new OrderRowViewModel(order, settings, now);

                // Status first, then the future promise. A pre-order that is ALREADY ready is still a
                // pre-order — it is not in the kitchen, it is finished and waiting for a time — and it
                // belongs to the operator looking at «По времени», not to the one handing orders over.
                if (order.Status == OrderStatus.Ready) ready.Add(row);
                else if (order.IsScheduledAt(now)) scheduled.Add(row);
                else preparing.Add(row);
            }

            // ARRIVAL ORDER, as the owner asked: hand the orders over in the order they came. The
            // promised time is on the card already, and a kitchen board that reorders by promise puts
            // a «К 18:00» order above a customer standing at the counter at 15:00.
            ready.Sort(CompareByArrival);
            preparing.Sort(CompareByArrival);

            // A pre-order is queued on its own tab AND pinned to the BOTTOM of «Готовятся», so a
            // «К 18:00» order can be started early without jumping ahead of anything actually owed.
            // Sorting the tail separately and re-adding keeps the two rules from fighting: the main
            // list stays in arrival order and only the pre-orders under it are in promise order.
            var due = preparing.Where(row => row.IsScheduled).ToList();
            var working = preparing.Where(row => !row.IsScheduled).ToList();
            working.Sort(CompareByArrival);
            due.Sort((left, right) => left.Model.PromisedAt.CompareTo(right.Model.PromisedAt));
            preparing = [.. working, .. due];

            // «По времени» is the one place promise order IS the right answer: the question there is
            // "what is coming", and the sequence of commitments is the thing being read.
            scheduled.Sort((left, right) => left.Model.PromisedAt.CompareTo(right.Model.PromisedAt));

            PreparingOrders.SyncWith(preparing, row => row.Model.Id);
            ReadyOrders.SyncWith(ready, row => row.Model.Id);
            ScheduledOrders.SyncWith(scheduled, row => row.Model.Id);
            AnnounceTabCounts();

            // Each card's total is formatted in the operator's currency, which can be changed on the
            // Settings tab while this board is alive — Shell keeps one instance per tab, so returning
            // here re-runs this method rather than rebuilding the ViewModel. Re-raise so a board left
            // open across a switch stops printing the previous sign. See
            // MenuViewModel.RefreshMoneyText for the same argument on the cart.
            foreach (var row in PreparingOrders) row.RefreshMoneyText();
            foreach (var row in ReadyOrders) row.RefreshMoneyText();
            foreach (var row in ScheduledOrders) row.RefreshMoneyText();

            if (clearMessage) Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load active orders");
            Message = UserMessages.Describe(exception, "Не удалось загрузить заказы");
        }
    }

    /// <summary>
    /// Shows one of the three tabs. A no-op on the tab already shown, so a second tap on the same chip
    /// neither re-raises the three visibility flags nor buzzes the haptic again.
    /// </summary>
    private void SelectTab(OrderTab tab)
    {
        if (tab == SelectedTab) return;

        SelectedTab = tab;
        haptics.Click();
    }

    /// <summary>
    /// Arrival order: the order that was taken first is first. Then by promised time, then by id, so
    /// the comparison is a total order and never returns 0 for two different rows.
    /// </summary>
    /// <remarks>
    /// The id tiebreak is not decoration. <see cref="List{T}.Sort(System.Comparison{T})"/> is not
    /// stable, so two orders created in the same tick — which SQLite does not distinguish to the
    /// second — could swap places on every auto-refresh tick. An operator would see cards flicker
    /// between refreshes with nothing having changed, and would stop trusting the order of the queue.
    /// </remarks>
    private static int CompareByArrival(OrderRowViewModel left, OrderRowViewModel right) =>
        left.Model.CreatedAt.CompareTo(right.Model.CreatedAt) is var byArrival and not 0
            ? byArrival
            : left.Model.PromisedAt.CompareTo(right.Model.PromisedAt) is var byPromise and not 0
                ? byPromise
                : left.Model.Id.CompareTo(right.Model.Id);

    /// <summary>
    /// Re-announces the three tab captions after a load. Called on every tick rather than only when a
    /// count changes, because <c>SyncWith</c> leaves the collections equal in size across most refreshes
    /// and the caption is a computed string that would otherwise keep a stale number on screen.
    /// </summary>
    private void AnnounceTabCounts()
    {
        OnPropertyChanged(nameof(PreparingTabText));
        OnPropertyChanged(nameof(ReadyTabText));
        OnPropertyChanged(nameof(ScheduledTabText));

        // The chip dot rides on the rows, so it has to be announced whenever they are rebuilt. Missing
        // this leaves the chip dot lit after the order underneath it was opened — the count would have
        // gone down but the dot would still claim there was something new.
        OnPropertyChanged(nameof(HasUnseenReady));
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

    /// <summary>
    /// Opens the order, and retires its unread dot on the way in.
    /// </summary>
    /// <remarks>
    /// The dot is retired by OPENING, which is the only act that can mean "seen": the operator has the
    /// contents on screen at that point, whatever else they have or have not done with them. Tying it to
    /// the status advance instead would clear the dot for whoever pressed «Готов» — the one person
    /// who by definition has not looked at the finished order yet.
    /// <para>
    /// The write is best-effort and the navigation happens first, so a failure costs the dot, not the
    /// navigation: an order that opens is far more important than a marker that survives. The next
    /// auto-refresh tick reloads the row from the database, so the dot disappears on its own either
    /// way — the re-load is what actually makes it vanish, this is what makes it permanent.
    /// </para>
    /// </remarks>
    private async Task OpenDetailsAsync(OrderRowViewModel? row)
    {
        if (row is null) return;

        await navigation.GoToOrderDetailsAsync(row.Model.Id);

        if (!row.IsUnseenReady) return;

        try
        {
            await orders.MarkSeenAsync(row.Model.Id);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to mark order {OrderId} seen", row.Model.Id);
        }
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

