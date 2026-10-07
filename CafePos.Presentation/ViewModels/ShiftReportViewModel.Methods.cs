using System.Globalization;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Shift report, shift closing and report export.</summary>
public partial class ShiftReportViewModel
{
    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            /// <summary>NO SHIFT IS A NORMAL STATE NOW, not something to paper over by creating one.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            var shift = await orders.GetActiveShiftAsync();
            await shiftSession.RefreshAsync();
            HasOpenShift = shift is not null;
            OnPropertyChanged(nameof(HasOpenShift));

            if (shift is null)
            {
                shiftId = Guid.Empty;
                ShiftHistory.Clear();
                CashMovements.Clear();
                // Cleared on this path too, not left holding the previous shift's rows: after a
                // close this ViewModel rebinds to nothing, and a «Скидки» list still showing the
                // shift that just ended would look like it belonged to the next one.
                DiscountedLines.Clear();
                OnPropertyChanged(nameof(HasNoShiftHistory));
                OnPropertyChanged(nameof(HasNoCashMovements));
                OnPropertyChanged(nameof(HasNoDiscountedLines));
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
            /// <summary>The change put in and the cash carried out, so the drawer line below is a SUM the operator can check on the screen instead of a number they have to trust.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            FloatCash = stats.FloatCash;
            PayoutCash = stats.PayoutCash;
            /// <summary>The net, copied from the one place it is computed.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            CashInDrawer = stats.ExpectedCashNow;

            // Every figure above is assigned in this one pass, so the seven formatted amounts the
            // page actually binds are re-raised together here. Setting a decimal raises only that
            // decimal; the *Text properties it feeds are a second binding and are told once.
            NotifyMoneyTexts();

            SyncMovements(await cashLedger.GetMovementsAsync(shift.Id));

            /// <summary>GetShiftOrderHistoryAsync (Completed AND Cancelled), not GetCompletedOrdersAsync.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            var now = DateTimeOffset.Now;
            var history = await orders.GetShiftOrderHistoryAsync(shift.Id);
            ShiftHistory.SyncWith(history.Select(order => new OrderRowViewModel(order, settings, now)), row => row.Model.Id);
            /// <summary>BindableLayout has no EmptyView, so «Закрытых заказов пока нет.» is a label bound to this flag.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            OnPropertyChanged(nameof(HasNoShiftHistory));

            /// <summary>The price control.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            SyncDiscountedLines(await orders.GetDiscountedLinesAsync(shift.Id));

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

    /// <summary>Brings the movements list in line with the domain, using only Add and Remove.</summary>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

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

    /// <summary>Brings the «Скидки» list in line with the domain.</summary>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

    private void SyncDiscountedLines(List<DiscountedLine> lines)
    {
        DiscountedLines.Clear();
        foreach (var line in lines)
            DiscountedLines.Add(new DiscountedLineRow(line, settings.OrderPrefix));

        OnPropertyChanged(nameof(HasNoDiscountedLines));
    }

    /// <summary>Voids a closed order from the history list — the entry point for a handed-over sale, since the orders board never lists a Completed one.</summary>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

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

    /// <summary>Closes the shift, and only after the operator has said what was physically in the drawer.</summary>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

    private async Task CloseShiftAsync()
    {
        try
        {
            /// <summary>Says what happens and nothing more.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            if (!await dialogs.ConfirmAsync("Закрыть смену?", "Текущая смена будет закрыта. Следующую нужно будет открыть заново.", "Закрыть смену", "Отмена"))
            {
                return;
            }

            var collected = await CollectCashCountAsync();
            if (collected is not { } entry)
            {
                return;
            }

            /// <summary>Captured BEFORE the close, and this is the whole reason the post-close export works at all.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            var closingShiftId = shiftId;
            var closingShiftStartedAt = startTime == default ? DateTimeOffset.Now : startTime;

            // Safety copy before the shift boundary: the cash register should never lose a day.
            await backups.CreateBackupAsync("Закрытие смены");
            var closed = await orders.CloseShiftAsync(entry.CountedKopecks, entry.Reason);

            // Only now is the count spent: it lives on the closed shift and there is no path that
            // rewrites it, which is why the retry pre-fill has to be dropped at this exact point.
            pendingCount = null;

            /// <summary>No shift is open now, and the terminal cannot be used until one is.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            shiftSession.SetKnownState(false, null);

            await LoadAsync();
            /// <summary>The count result is the one thing the operator cannot read off the screen they are on: a balance is stated here and then, for a shift with no orders, there is n...</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            Message = $"Смена закрыта в {closed.EndTime?.ToLocalTime():HH:mm}. "
                      + CashWording.Describe(entry.CountedKopecks - entry.ExpectedKopecks);

            /// <summary>The report of the shift that was just closed, NOT the archive.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            if (await dialogs.ConfirmAsync("Экспорт отчёта?",
                    "Отправить отчёт по закрытой смене с пересчётом кассы?", "Отправить", "Позже"))
            {
                var csv = await reports.ExportShiftReportCsvAsync(closingShiftId);
                var path = await files.SaveTextReportAsync(
                    $"shift-{closingShiftStartedAt.ToLocalTime():yyyyMMdd-HHmm}.csv", csv);
                await files.ShareFileAsync(path, "Отчёт смены");
            }

            /// <summary>LAST, after the export question, because that dialog is the only thing left that belongs to the shift being closed.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

    /// <summary>Asks for the counted cash and, when it does not match, for the reason.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

    private async Task<CashCountEntry?> CollectCashCountAsync()
    {
        // Exact round trip: ExpectedCashNow is Money.FromKopecks of the ledger's kopeck figure, and
        // ToKopecks reverses that division without rounding, so this is the ledger's number itself
        // rather than a re-derived approximation of it.
        var expectedKopecks = Money.ToKopecks(CashInDrawer);
        var expected = Money.FromKopecks(expectedKopecks);

        /// <summary>A previous entry wins over the live figure ONLY while the drawer it was counted against is the same drawer.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

        /// <summary>DisplayPromptAsync returns null for BOTH "Отмена" and an empty field, so the two cannot be told apart and neither is guessed at here — the same refusal OrderDet...</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        /// <summary>Group separators are removed before parsing, not after.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

        /// <summary>Pre-checked against the figure already on screen, so a matching count never shows the reason dialog — demanding a reason for an exact drawer would train the ope...</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

    /// <summary>The reason for a mismatch, or null when the operator did not give a usable one.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

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
            /// <summary>Pre-checked here even though the domain refuses an over-long reason too.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            Message = $"Причина расхождения длиннее {MaxDiscrepancyReasonLength} символов. Смена не закрыта.";
            return null;
        }

        return trimmed;
    }

    /// <summary>What the operator entered at the close prompt, in the units the domain takes.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    private readonly record struct CashCountEntry(long CountedKopecks, long ExpectedKopecks, string? Reason);

    /// <summary>Operator-facing text for a refused close.</summary>
    /// <remarks>Почему так — `docs/decisions/shift-report.md`</remarks>

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
