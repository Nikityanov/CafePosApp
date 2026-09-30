using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

public partial class ShiftReportViewModel : ObservableObject
{
    private readonly IOrderService orders;
    private readonly IReportExportService reports;
    private readonly IBackupService backups;
    private readonly IFileService files;
    private readonly IDialogService dialogs;
    private readonly INavigationService navigation;
    private readonly AppSettings settings;
    private readonly ILogger<ShiftReportViewModel> logger;

    public ShiftReportViewModel(
        IOrderService orders,
        IReportExportService reports,
        IBackupService backups,
        IFileService files,
        IDialogService dialogs,
        INavigationService navigation,
        AppSettings settings,
        ILogger<ShiftReportViewModel> logger)
    {
        this.orders = orders;
        this.reports = reports;
        this.backups = backups;
        this.files = files;
        this.dialogs = dialogs;
        this.navigation = navigation;
        this.settings = settings;
        this.logger = logger;

        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenAnalyticsCommand = new AsyncRelayCommand(() => navigation.GoToTabAsync("shift-analytics"));
        CloseShiftCommand = new AsyncRelayCommand(CloseShiftAsync);
        ExportShiftCommand = new AsyncRelayCommand(ExportShiftAsync);
    }

    public ObservableCollection<OrderRowViewModel> CompletedOrders { get; } = [];

    private Guid shiftId;
    private decimal revenue;
    public decimal Revenue { get => revenue; private set => SetProperty(ref revenue, value); }

    private int closedOrdersCount;
    public int ClosedOrdersCount { get => closedOrdersCount; private set => SetProperty(ref closedOrdersCount, value); }

    private int cancelledOrdersCount;
    public int CancelledOrdersCount { get => cancelledOrdersCount; private set => SetProperty(ref cancelledOrdersCount, value); }

    private int openOrdersCount;
    public int OpenOrdersCount { get => openOrdersCount; private set => SetProperty(ref openOrdersCount, value); }

    private decimal averageCheck;
    public decimal AverageCheck { get => averageCheck; private set => SetProperty(ref averageCheck, value); }

    // Matches ShiftAnalyticsViewModel: a worded empty state, not a bare dash.
    private string peakHour = "нет данных";
    public string PeakHour { get => peakHour; private set => SetProperty(ref peakHour, value); }

    private DateTimeOffset startTime;
    public string StartTimeText => startTime == default ? string.Empty : $"Смена открыта {startTime.ToLocalTime():dd.MM.yyyy HH:mm}";

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand OpenAnalyticsCommand { get; }
    public IAsyncRelayCommand CloseShiftCommand { get; }
    public IAsyncRelayCommand ExportShiftCommand { get; }
}
