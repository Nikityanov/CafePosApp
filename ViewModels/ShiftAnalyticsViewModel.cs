using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

public sealed record ShiftChoice(Guid Id, string DisplayName);

public sealed record ProductAnalyticsRow(string ProductName, string ModifierName, int Quantity, decimal Revenue);

public class ShiftAnalyticsViewModel : ObservableObject
{
    private readonly IOrderService orders;
    private readonly ILogger<ShiftAnalyticsViewModel> logger;

    private double averagePreparationMinutes;
    private double averageCompletionMinutes;

    public ShiftAnalyticsViewModel(IOrderService orders, ILogger<ShiftAnalyticsViewModel> logger)
    {
        this.orders = orders;
        this.logger = logger;
        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
    }

    public ObservableCollection<ShiftChoice> Shifts { get; } = [];
    public ObservableCollection<ProductAnalyticsRow> ProductRows { get; } = [];

    private ShiftChoice? selectedShift;
    public ShiftChoice? SelectedShift { get => selectedShift; set => SetProperty(ref selectedShift, value); }

    private decimal revenue;
    public decimal Revenue { get => revenue; private set => SetProperty(ref revenue, value); }

    private int ordersCount;
    public int OrdersCount { get => ordersCount; private set => SetProperty(ref ordersCount, value); }

    private int cancelledOrdersCount;
    public int CancelledOrdersCount { get => cancelledOrdersCount; private set => SetProperty(ref cancelledOrdersCount, value); }

    private int allOrdersCount;
    public int AllOrdersCount { get => allOrdersCount; private set => SetProperty(ref allOrdersCount, value); }

    private decimal averageCheck;
    public decimal AverageCheck { get => averageCheck; private set => SetProperty(ref averageCheck, value); }

    private int itemsCount;
    public int ItemsCount { get => itemsCount; private set => SetProperty(ref itemsCount, value); }

    public string AveragePreparationTime => TextFormat.Duration(averagePreparationMinutes);
    public string AverageCompletionTime => TextFormat.Duration(averageCompletionMinutes);

    // "нет данных", not a bare dash: the duration cards on the same screen (Среднее
    // приготовление, Среднее выполнение) already say exactly that, and a lone "—" beside two
    // worded empty states read as a missing value rather than a measured absence.
    private string peakHour = "нет данных";
    public string PeakHour { get => peakHour; private set => SetProperty(ref peakHour, value); }

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand AnalyzeCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var previousId = SelectedShift?.Id;
            var shifts = await orders.GetShiftsAsync();

            Shifts.SyncWith(
                shifts.Select(shift => new ShiftChoice(shift.Id,
                    $"{(shift.IsActive ? "Текущая" : "Завершённая")} · {shift.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}" +
                    $"{(shift.EndTime.HasValue ? $" – {shift.EndTime.Value.ToLocalTime():HH:mm}" : string.Empty)}")),
                choice => choice.Id);

            SelectedShift = Shifts.FirstOrDefault(choice => choice.Id == previousId) ?? Shifts.FirstOrDefault();
            await AnalyzeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось загрузить аналитику");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AnalyzeAsync()
    {
        if (SelectedShift is null) return;

        try
        {
            var stats = await orders.GetShiftStatsAsync(SelectedShift.Id);
            AllOrdersCount = stats.AllOrdersCount;
            CancelledOrdersCount = stats.CancelledCount;
            Revenue = stats.Revenue;
            OrdersCount = stats.CompletedCount;
            AverageCheck = stats.AverageCheck;
            ItemsCount = stats.ItemsCount;
            averagePreparationMinutes = stats.AveragePreparationMinutes;
            averageCompletionMinutes = stats.AverageCompletionMinutes;
            PeakHour = stats.PeakHour;

            OnPropertyChanged(nameof(AveragePreparationTime));
            OnPropertyChanged(nameof(AverageCompletionTime));

            var rows = await orders.GetProductAnalyticsAsync(SelectedShift.Id);
            ProductRows.SyncWith(
                rows.Select(row => new ProductAnalyticsRow(row.ProductName, row.ModifierName, row.Quantity, row.Revenue)),
                row => $"{row.ProductName}|{row.ModifierName}");

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build the shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось построить аналитику");
        }
    }
}
