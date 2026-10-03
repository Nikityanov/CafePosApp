using System.Globalization;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>Shift report, shift closing and report export.</summary>
public partial class ShiftReportViewModel
{
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // NO SHIFT IS A NORMAL STATE NOW, not something to paper over by creating one. This screen is what
            // the operator comes to when there is nothing open, and it offers to open a shift rather
            // than quietly opening one with no float — which is the state the whole cash ledger
            // feature exists to stop.
            var shift = await orders.GetActiveShiftAsync();
            await shiftSession.RefreshAsync();
            HasOpenShift = shift is not null;
            OnPropertyChanged(nameof(HasOpenShift));

            if (shift is null)
            {
                shiftId = Guid.Empty;
                ShiftHistory.Clear();
                CashMovements.Clear();
                OnPropertyChanged(nameof(HasNoShiftHistory));
                OnPropertyChanged(nameof(HasNoCashMovements));
                OnPropertyChanged(nameof(StartTimeText));
                Message = "Смена не открыта.";
                return;
            }

            shiftId = shift.Id;
            startTime = shift.StartTime;
            OnPropertyChanged(nameof(StartTimeText));

            // Same aggregates as the analytics page — one source of truth for all numbers.
            var stats = await orders.GetShiftStatsAsync(shift.Id);
            Revenue = stats.Revenue;
            ClosedOrdersCount = stats.CompletedCount;
            CancelledOrdersCount = stats.CancelledCount;
            OpenOrdersCount = stats.OpenCount;
            AverageCheck = stats.AverageCheck;
            PeakHour = stats.PeakHour;

            // The money through the till. Assigned from the same ShiftStats the CSV export reads, so
            // the screen and the export cannot disagree about what the drawer did — that pairing is
            // the entire reason these lines are here.
            PaymentsCash = stats.PaymentsCash;
            PaymentsCard = stats.PaymentsCard;
            RefundsCash = stats.RefundsCash;
            RefundsCard = stats.RefundsCard;
            // The change put in and the cash carried out, so the drawer line below is a SUM the
            // operator can check on the screen instead of a number they have to trust. Both are net
            // of correcting entries, which is why an uncorrected mistake and a corrected one show the
            // same figure here and differ only in the list underneath.
            FloatCash = stats.FloatCash;
            PayoutCash = stats.PayoutCash;
            // The net, copied from the one place it is computed. It used to be
            // PaymentsCash - RefundsCash written here, plus a hand-written
            // OnPropertyChanged(nameof(CashInDrawer)) to re-notify a derived property; both are gone,
            // because a drawer figure that the presentation layer recomputes is a second definition
            // of it, and the shift close now compares a stored count against this number.
            CashInDrawer = stats.ExpectedCashNow;

            // Every figure above is assigned in this one pass, so the seven formatted amounts the
            // page actually binds are re-raised together here. Setting a decimal raises only that
            // decimal; the *Text properties it feeds are a second binding and are told once.
            NotifyMoneyTexts();

            SyncMovements(await cashLedger.GetMovementsAsync(shift.Id));

            // GetShiftOrderHistoryAsync (Completed AND Cancelled), not GetCompletedOrdersAsync.
            // Cancelling a paid order flips it to Cancelled, so under the old query the one sale a
            // manager most needs to see after a bad void vanished from the only list they read.
            var history = await orders.GetShiftOrderHistoryAsync(shift.Id);
            ShiftHistory.SyncWith(history.Select(order => new OrderRowViewModel(order, settings)), row => row.Model.Id);
            // BindableLayout has no EmptyView, so «Закрытых заказов пока нет.» is a label bound to
            // this flag. The flag is derived from the count and therefore notifies itself when it
            // has to — but only if something asks it to, and SyncWith raises collection changes
            // without consulting the ViewModel. This is the one line that keeps a shift with no
            // closed orders from rendering a bare section heading.
            OnPropertyChanged(nameof(HasNoShiftHistory));

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the shift report");
            Message = UserMessages.Describe(exception, "Не удалось загрузить смену");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Brings the movements list in line with the domain, using only Add and Remove.
    /// </summary>
    /// <remarks>
    /// NOT <c>ObservableCollectionSync.SyncWith</c>, and at the time of writing the reason was a crash
    /// rather than a preference. <c>SyncWith</c> replaced a row whose key was unchanged but whose
    /// instance differed, which is every reload here because these rows are classes; MAUI's
    /// <c>BindableLayoutController.ReplaceChild</c> indexes into its own list of realised children and
    /// throws <c>ArgumentOutOfRangeException</c> when a reload happens before that list exists. The
    /// exception was raised inside this reload, caught by the page's own handler and shown to the
    /// operator as «Не удалось загрузить смену» — with the drawer figures above it already updated,
    /// which is the worst shape a partial failure can take.
    /// <para>
    /// <c>SyncWith</c> no longer raises Replace at all — it re-creates a changed row AT ITS INDEX or
    /// refreshes it through a delegate — so it would be safe here too. This method stays anyway,
    /// because for THIS list it is the clearer statement of what the list can do: a movement is
    /// written once and never edited, a correction is a new row rather than a change to an old one,
    /// and so the only two operations the list ever needs are Add and Remove. Add/Remove/Reset are
    /// the actions BindableLayout applies through layout.Insert / layout.RemoveAt / CreateChildren(),
    /// so nothing here depends on the collection and its realised children agreeing by index.
    /// </para>
    /// <para>
    /// The reason it is not <c>SyncWith</c> was never that a movement cannot change in place. It is
    /// that <c>OrderRowViewModel</c> — the row <see cref="ShiftHistory"/> is built from — has
    /// <c>Model { get; }</c> with computed properties, so there is nothing on it to write: a changed
    /// row can only be replaced by a NEW instance. <c>SyncWith</c> now does exactly that at the same
    /// index, which is why the history list needed no second sync method to become safe.
    /// </para>
    /// <para>
    /// O(n²) over a handful of rows, which is not a cost worth optimising away.
    /// </para>
    /// </remarks>
    private void SyncMovements(IReadOnlyList<CashMovement> movements)
    {
        var incoming = movements.Select(movement => movement.Id).ToHashSet();

        for (var index = CashMovements.Count - 1; index >= 0; index--)
        {
            if (!incoming.Contains(CashMovements[index].Id)) CashMovements.RemoveAt(index);
        }

        foreach (var movement in movements)
        {
            if (CashMovements.All(row => row.Id != movement.Id)) CashMovements.Add(new CashMovementRow(movement));
        }

        OnPropertyChanged(nameof(HasNoCashMovements));
    }

    /// <summary>
    /// Voids a closed order from the history list — the entry point for a handed-over sale, since
    /// the orders board never lists a Completed one.
    /// </summary>
    /// <remarks>
    /// Deliberately the same three dialogs in the same order as the board's cancel flow
    /// (confirm, disposition, reason) and with the same wording, because they are the same
    /// operation. The difference is entirely in what it costs: here the order is finished and paid,
    /// so the confirmation states the amount that goes back and the outcome message reports it.
    /// <para>
    /// The page reloads rather than patching the row. A cancellation writes refund rows to the
    /// ledger, changes PaidKopecks and can move stock; the row on screen shows status and payment
    /// state, and a half-updated card is worse than a re-read one. It is also the only way the
    /// money block above the list can pick up the refund it just caused.
    /// </para>
    /// </remarks>
    private async Task CancelOrderAsync(OrderRowViewModel? row)
    {
        if (row is null) return;

        try
        {
            var paid = row.Model.PaidKopecks;
            var paidText = TextFormat.Money(Money.FromKopecks(paid));
            var summary = paid > 0
                ? $"{row.OrderTitle} на {TextFormat.Money(row.TotalPrice)} будет отменён, клиенту вернётся {paidText}."
                : $"{row.OrderTitle} на {TextFormat.Money(row.TotalPrice)} будет отменён.";

            if (!await dialogs.ConfirmAsync("Отменить заказ?", summary, row.CancelText, "Назад"))
            {
                return;
            }

            var stock = await stockDisposition.ChooseAsync(new StockDispositionSheetRequest(
                row.OrderTitle,
                row.TotalPrice,
                Money.FromKopecks(paid)));
            if (stock is null) return;

            var reason = await dialogs.PromptAsync("Причина отмены", "Необязательно: причина отмены заказа", string.Empty);
            await orders.CancelOrderAsync(row.Model.Id, reason, stock.Value);

            await LoadAsync();
            Message = paid > 0
                ? $"{row.OrderTitle} отменён, возвращено {paidText}."
                : $"{row.OrderTitle} отменён.";
            haptics.Warn();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to cancel order {OrderId} from the shift report", row.Model.Id);
            Message = UserMessages.Describe(exception, "Не удалось отменить заказ");
            haptics.Warn();
        }
    }

    /// <summary>
    /// Closes the shift, and only after the operator has said what was physically in the drawer.
    /// </summary>
    /// <remarks>
    /// The count is not a step in this flow, it IS the flow: <c>CloseShiftAsync</c> takes the
    /// counted amount and refuses a shift closed without one, so a shift that closes has a
    /// reconciliation attached to it. There is no "skip" branch and none is wanted — a count that
    /// can be declined is a count that does not exist.
    /// <para>
    /// ORDER OF OPERATIONS, and the order is the design. Confirm, then count, then reason, then
    /// backup, then close:
    /// <list type="bullet">
    ///   <item>The count is asked for BEFORE the backup, because the backup is wasted work if the
    ///   operator then abandons the dialog — and the dialogs are the only part of this the operator
    ///   can genuinely back out of.</item>
    ///   <item>The count is asked for before the close, not after, so a mismatch is discovered while
    ///   the drawer is still on the table and the operator can count it again. Discovering a
    ///   shortage after the fact, with the money in a bank bag, is the worst order available.</item>
    ///   <item>The backup stays immediately before the write, which is the only moment it can protect
    ///   anything: everything after it is either a dialog or the reload.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The PROMPT IS PRE-FILLED with the live drawer figure. In the overwhelmingly common case the
    /// count matches, and then the whole reconciliation costs one tap on a value the app already
    /// knows. Hand-transcribing a figure the screen is already showing is a transcription task, and
    /// transcription fatigue has a known failure mode: after a dozen shifts of typing zeroes into
    /// fields that are all the same shape, the zeros keep coming. Removing the typing is the
    /// cheapest accuracy win available anywhere in this feature.
    /// </para>
    /// <para>
    /// The pre-fill deliberately shows the figure ON SCREEN, not a freshly re-read one. A pre-fill
    /// that disagreed with the «Итого наличными в кассе» line a few centimetres above the button
    /// would be a number the operator cannot reason about; if the screen is stale they press
    /// «Обновить». The failure mode of a stale screen is benign anyway — the domain compares
    /// against the truth and answers with both figures, and the operator corrects the count.
    /// </para>
    /// </remarks>
    private async Task CloseShiftAsync()
    {
        try
        {
            // Says what happens and nothing more. It used to promise "и открыта новая", because the
            // close did open one; it does not any more, and a dialog that describes a behaviour the
            // app no longer has is worse than no dialog at all — the operator waits for a shift that
            // never arrives. The terminal sits empty until someone opens the next one, which is the
            // whole point of counting the drawer onto paper first.
            if (!await dialogs.ConfirmAsync("Закрыть смену?", "Текущая смена будет закрыта. Следующую нужно будет открыть заново.", "Закрыть смену", "Отмена"))
            {
                return;
            }

            var collected = await CollectCashCountAsync();
            if (collected is not { } entry)
            {
                return;
            }

            // Captured BEFORE the close, and this is the whole reason the post-close export works at
            // all. The close ends the shift and the reload below rebinds this ViewModel to whatever
            // is open - which, right after a close, is nothing - so `shiftId`, the id every export in
            // this ViewModel reads, would be empty. Exporting against that produced a CSV of a
            // shift with no orders in it, which is how a reconciliation became unexportable: the
            // one file that carries the counted cash was the file about the wrong shift.
            //
            // The start time is captured for the same reason: the file is named after the SHIFT it
            // describes, not after the moment it was exported, or every shift closed within the same
            // hour would be ambiguous on the device it landed on. The default is reachable — a load
            // that failed leaves startTime unset while the button is still live — and a report named
            // shift-00010101-0000.csv helps nobody, so it falls back to now rather than to a lie
            // about when the shift began.
            var closingShiftId = shiftId;
            var closingShiftStartedAt = startTime == default ? DateTimeOffset.Now : startTime;

            // Safety copy before the shift boundary: the cash register should never lose a day.
            await backups.CreateBackupAsync("Закрытие смены");
            var closed = await orders.CloseShiftAsync(entry.CountedKopecks, entry.Reason);

            // Only now is the count spent: it lives on the closed shift and there is no path that
            // rewrites it, which is why the retry pre-fill has to be dropped at this exact point.
            pendingCount = null;

            // No shift is open now, and the terminal cannot be used until one is. The cache is
            // updated BEFORE the reload so the page renders the closed state rather than the last
            // open one, and the opening screen pre-fills the counted drawer - the number the
            // operator has just written on paper.
            shiftSession.SetKnownState(false, null);

            await LoadAsync();
            // The count result is the one thing the operator cannot read off the screen they are on:
            // a balance is stated here and then, for a shift with no orders, there is nothing left
            // on this page to compare it against. The time is the moment the shift ENDED, which is
            // what it always was — it was being labelled as the start of the next shift, which never
            // came.
            Message = $"Смена закрыта в {closed.EndTime?.ToLocalTime():HH:mm}. "
                      + CashWording.Describe(entry.CountedKopecks - entry.ExpectedKopecks);

            // The report of the shift that was just closed, NOT the archive. The archive is still
            // available from Настройки, so nothing is lost by asking here instead — but the
            // archive's own shift-report.csv is written from whatever shift it is handed, and after
            // a close there is no open shift to hand it, so the reconciliation was never in it.
            // This CSV is the only artefact that carries the count, and it is about the right shift.
            if (await dialogs.ConfirmAsync("Экспорт отчёта?",
                    "Отправить отчёт по закрытой смене с пересчётом кассы?", "Отправить", "Позже"))
            {
                var csv = await reports.ExportShiftReportCsvAsync(closingShiftId);
                var path = await files.SaveTextReportAsync(
                    $"shift-{closingShiftStartedAt.ToLocalTime():yyyyMMdd-HHmm}.csv", csv);
                await files.ShareFileAsync(path, "Отчёт смены");
            }

            // LAST, after the export question, because that dialog is the only thing left that belongs
            // to the shift being closed. Then the opening screen, for the same reason the startup path
            // shows it: the terminal cannot be used until a shift exists, and its field arrives
            // pre-filled with the figure the operator just wrote on paper. Staying on this page would
            // leave them looking at "Смена не открыта" with a button, which is the same fact said more
            // weakly.
            await navigation.GoToOpenShiftAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to close the shift");
            // Reloaded BEFORE the message, not after: a refused close is nearly always "orders are
            // still open", and the operator has to be able to see them. LoadAsync clears Message on
            // success, so the order of these two lines is load-bearing.
            await LoadAsync();
            Message = DescribeCloseFailure(exception);
            haptics.Warn();
        }
    }

    /// <summary>
    /// Asks for the counted cash and, when it does not match, for the reason. Returns null when the
    /// operator backed out or the entry was unusable, in which case nothing at all has been written.
    /// </summary>
    /// <remarks>
    /// Two failure modes are handled differently on purpose. An ABORT (whitespace, an unparseable
    /// number, a missing reason) returns quietly and writes nothing: the shift is untouched and the
    /// operator decides when to try again. A DOMAIN REFUSAL is left to propagate to the caller's
    /// catch, because by then the count is on its way into the shift and the operator has to be told
    /// why it is not there.
    /// <para>
    /// The expectation is carried back in the entry next to the count. The caller needs it to word
    /// the outcome message, and reading it off the ViewModel after the close would mean reading
    /// <c>CashInDrawer</c> — a value <c>LoadAsync</c> is about to overwrite with the NEW shift's
    /// figures. Carrying the pair means the message cannot be assembled from a number that moved.
    /// </para>
    /// </remarks>
    private async Task<CashCountEntry?> CollectCashCountAsync()
    {
        // Exact round trip: ExpectedCashNow is Money.FromKopecks of the ledger's kopeck figure, and
        // ToKopecks reverses that division without rounding, so this is the ledger's number itself
        // rather than a re-derived approximation of it.
        var expectedKopecks = Money.ToKopecks(CashInDrawer);
        var expected = Money.FromKopecks(expectedKopecks);

        // A previous entry wins over the live figure ONLY while the drawer it was counted against is
        // the same drawer. After a refused close the operator is being asked the same question about
        // the same money, and their answer is still true — unless the drawer moved while they were
        // away closing orders, which is exactly what happens after that refusal. So the previous entry
        // carries the figure it was counted against and is used only when that still matches; otherwise
        // the live figure, which the sentence above already states.
        //
        // WHICH entry to offer is decided in the core (CashCountPrefill.Resolve) and only formatted
        // here: the rule is tested rather than trusted, and no arithmetic about money lives in a
        // dialog. This ViewModel no longer knows how to tell a fresh count from a stale one.
        var prefillKopecks = CashCountPrefill.Resolve(pendingCount, expectedKopecks);

        // "0.00" rather than "F2" on purpose: F2 in ru-RU groups the thousands ("4 320,00"), and a
        // numeric prompt is not the place for a group separator the soft keyboard cannot reliably
        // reproduce. The readable grouped form is in the message text instead.
        var prefill = Money.FromKopecks(prefillKopecks).ToString("0.00", CultureInfo.CurrentCulture);

        var entry = await dialogs.PromptAsync(
            "Пересчёт кассы",
            $"Сколько наличных в кассе? По учёту {TextFormat.Money(expected)}. " +
            "Введите фактическую сумму — 0 тоже подходит, пустая касса это расхождение.",
            prefill,
            "Закрыть смену",
            "Отмена");

        // DisplayPromptAsync returns null for BOTH "Отмена" and an empty field, so the two cannot be
        // told apart and neither is guessed at here — the same refusal OrderDetailsViewModel makes
        // on the refund reason. Whitespace is therefore an abort, and the count is not written: a
        // close the operator cancelled must not leave a reconciliation behind.
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        // Group separators are removed before parsing, not after. ru-RU's is U+00A0, a hand-typed
        // "4 320,00" is a realistic input, and NumberStyles accepts a thousands separator only in
        // the culture's own form — rejecting a number of the shape the app itself displays would be
        // a self-inflicted wound. Everything else is left to TextFormat.TryParseDecimal, which
        // handles the comma/dot ambiguity.
        var normalized = entry.Replace(" ", string.Empty).Replace("\u00a0", string.Empty);
        if (!TextFormat.TryParseDecimal(normalized, out var amount))
        {
            Message = "Пересчёт кассы должен быть числом. Смена не закрыта.";
            return null;
        }

        // Refused here as well as in the domain, and for the same reason the domain refuses it: a
        // negative drawer does not exist. Saying so in one line beats letting Money.ToKopecks round
        // it to a plausible-looking negative count on its way to an exception.
        if (amount < 0)
        {
            Message = "Пересчёт кассы не может быть отрицательным. Смена не закрыта.";
            return null;
        }

        var countedKopecks = Money.ToKopecks(amount);
        string? reason = null;

        // Pre-checked against the figure already on screen, so a matching count never shows the
        // reason dialog — demanding a reason for an exact drawer would train the operator to type
        // filler into an audit field. The domain re-checks against the truth and throws if the two
        // disagree, which is the point: this is a UX pre-check, not the rule, and the rule is the
        // domain's.
        if (countedKopecks != expectedKopecks)
        {
            reason = await PromptDiscrepancyReasonAsync(expected, countedKopecks);
            if (reason is null)
            {
                return null;
            }
        }

        // Retained BEFORE the close is attempted, and only now: see the remark on
        // pendingCount for why the retry must not cost the operator their count, and why the figure
        // it was counted against travels with it.
        pendingCount = new PendingCashCount(countedKopecks, expectedKopecks);
        return new CashCountEntry(countedKopecks, expectedKopecks, reason);
    }

    /// <summary>
    /// The reason for a mismatch, or null when the operator did not give a usable one. Required by
    /// the domain and asked for as such, because an unexplained shortage is the one entry nobody
    /// can reconstruct a week later.
    /// </summary>
    /// <remarks>
    /// Both figures are repeated in the prompt. The operator is being asked to explain a specific
    /// number, and a prompt that says only "причина расхождения" invites a reason for the wrong
    /// discrepancy — the one they remember from this morning, not the one on the screen.
    /// </remarks>
    private async Task<string?> PromptDiscrepancyReasonAsync(decimal expected, long countedKopecks)
    {
        var difference = countedKopecks - Money.ToKopecks(expected);
        var reason = await dialogs.PromptAsync(
            "Причина расхождения",
            $"Обязательно: {CashWording.Describe(difference).ToLowerInvariant()}. " +
            $"В кассе {TextFormat.Money(Money.FromKopecks(countedKopecks))} против {TextFormat.Money(expected)} по учёту. " +
            "Это поле аудита — сокращать его нельзя.",
            string.Empty,
            "Закрыть смену",
            "Назад");

        if (string.IsNullOrWhiteSpace(reason))
        {
            Message = "Смена не закрыта: не указана причина расхождения.";
            return null;
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > MaxDiscrepancyReasonLength)
        {
            // Pre-checked here even though the domain refuses an over-long reason too. The domain's
            // refusal is correct and must not be weakened — this string is the only record of why a
            // drawer did not balance, and DisplayPromptAsync has no MaxLength to stop a paste — but
            // it costs the operator the entire dialog to learn it, and pasting 300+ characters into
            // a phone prompt is not a rare accident. Mirrors the private
            // OrderService.MaxDiscrepancyReasonLength; the domain stays the authority, this only
            // keeps what was typed on screen instead of throwing it away.
            Message = $"Причина расхождения длиннее {MaxDiscrepancyReasonLength} символов. Смена не закрыта.";
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// What the operator entered at the close prompt, in the units the domain takes.
    /// </summary>
    /// <param name="CountedKopecks">The physical count. 0 is a real count, not an absent one.</param>
    /// <param name="ExpectedKopecks">
    /// The live drawer figure the count was compared against, carried back so the outcome message
    /// can be worded from the pair that was actually submitted.
    /// </param>
    /// <param name="Reason">Set iff <paramref name="CountedKopecks"/> differs from the expectation.</param>
    private readonly record struct CashCountEntry(long CountedKopecks, long ExpectedKopecks, string? Reason);

    /// <summary>
    /// Operator-facing text for a refused close.
    /// </summary>
    /// <remarks>
    /// An <see cref="AppException"/> from this flow is already a Russian sentence written for the
    /// operator — «Нельзя закрыть смену: осталось незакрытых заказов — 3, из них не оплачено 2 на
    /// 120,00 ₽» — so it is shown on its own. Prefixing it with the generic «Не удалось закрыть
    /// смену: » produced "Не удалось закрыть смену: Нельзя закрыть смену: …", which reads as a
    /// stutter and buries the part that matters. Every other failure still goes through
    /// <see cref="UserMessages.Describe"/>, whose last arm is what keeps an unknown error from
    /// reaching the operator as a bare type name.
    /// </remarks>
    private static string DescribeCloseFailure(Exception exception) => exception switch
    {
        AppException app when !string.IsNullOrWhiteSpace(app.Message) => app.Message,
        _ => UserMessages.Describe(exception, "Не удалось закрыть смену")
    };

    private async Task ExportShiftAsync()
    {
        try
        {
            var csv = await reports.ExportShiftReportCsvAsync(shiftId);
            var path = await files.SaveTextReportAsync($"shift-{DateTime.Now:yyyyMMdd-HHmm}.csv", csv);
            await files.ShareFileAsync(path, "Отчёт смены");
            Message = "Отчёт смены отправлен.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export the shift report");
            Message = UserMessages.Describe(exception, "Не удалось экспортировать отчёт");
        }
    }
}
