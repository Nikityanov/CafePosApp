using CafePos.Core.Common;
using CafePos.Core.Errors;
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
            var shift = await orders.GetOrCreateActiveShiftAsync();
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
            // CashInDrawer is computed from the two above, so it has to be re-notified by hand.
            OnPropertyChanged(nameof(CashInDrawer));

            // GetShiftOrderHistoryAsync (Completed AND Cancelled), not GetCompletedOrdersAsync.
            // Cancelling a paid order flips it to Cancelled, so under the old query the one sale a
            // manager most needs to see after a bad void vanished from the only list they read.
            var history = await orders.GetShiftOrderHistoryAsync(shift.Id);
            ShiftHistory.SyncWith(history.Select(order => new OrderRowViewModel(order, settings)), row => row.Model.Id);

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

    private async Task CloseShiftAsync()
    {
        try
        {
            if (!await dialogs.ConfirmAsync("Закрыть смену?", "Текущая смена будет закрыта и открыта новая.", "Закрыть смену", "Отмена"))
            {
                return;
            }

            // Safety copy before the shift boundary: the cash register should never lose a day.
            await backups.CreateBackupAsync("Закрытие смены");
            var next = await orders.CloseShiftAsync();
            await LoadAsync();
            Message = $"Смена закрыта. Новая смена открыта {next.StartTime.ToLocalTime():HH:mm}.";

            if (await dialogs.ConfirmAsync("Экспорт данных?", "Отправить архив с базой и отчётами?", "Отправить", "Позже"))
            {
                var path = await backups.ExportArchiveAsync();
                await files.ShareFileAsync(path, "Экспорт CafePOS");
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to close the shift");
            Message = UserMessages.Describe(exception, "Не удалось закрыть смену");
        }
    }

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
