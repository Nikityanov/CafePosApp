using CafePos.Core.Common;
using CafePos.Core.Errors;
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

            var completed = await orders.GetCompletedOrdersAsync(shift.Id);
            CompletedOrders.SyncWith(completed.Select(order => new OrderRowViewModel(order, settings)), row => row.Model.Id);

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
