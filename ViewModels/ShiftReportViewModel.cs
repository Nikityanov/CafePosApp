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
    private readonly IStockDispositionSheet stockDisposition;
    private readonly IHapticService haptics;
    private readonly ILogger<ShiftReportViewModel> logger;

    public ShiftReportViewModel(
        IOrderService orders,
        IReportExportService reports,
        IBackupService backups,
        IFileService files,
        IDialogService dialogs,
        INavigationService navigation,
        AppSettings settings,
        IStockDispositionSheet stockDisposition,
        IHapticService haptics,
        ILogger<ShiftReportViewModel> logger)
    {
        this.orders = orders;
        this.reports = reports;
        this.backups = backups;
        this.files = files;
        this.dialogs = dialogs;
        this.navigation = navigation;
        this.settings = settings;
        this.stockDisposition = stockDisposition;
        this.haptics = haptics;
        this.logger = logger;

        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenAnalyticsCommand = new AsyncRelayCommand(() => navigation.GoToTabAsync("shift-analytics"));
        CloseShiftCommand = new AsyncRelayCommand(CloseShiftAsync);
        ExportShiftCommand = new AsyncRelayCommand(ExportShiftAsync);
        CancelOrderCommand = new AsyncRelayCommand<OrderRowViewModel>(CancelOrderAsync);
        OpenDetailsCommand = new AsyncRelayCommand<OrderRowViewModel>(row =>
            row is null ? Task.CompletedTask : navigation.GoToOrderDetailsAsync(row.Model.Id));
    }

    /// <summary>
    /// The shift's closed orders: Completed AND Cancelled.
    /// </summary>
    /// <remarks>
    /// Fed by <c>IOrderService.GetShiftOrderHistoryAsync</c>, not
    /// <c>GetCompletedOrdersAsync</c>. The report is the list a manager reads after the fact, and a
    /// voided sale that silently disappeared from it was exactly the one they most needed to see —
    /// the whole point of cancelling a paid order is that money left the till, and hiding the order
    /// hid the evidence. Voided orders now show AS voided, with the cancel action still on them.
    /// <para>
    /// Renamed from <c>CompletedOrders</c> because the name became false the moment cancelled
    /// orders joined it, and a binding that says "CompletedOrders" while carrying cancelled ones
    /// is a trap for whoever edits this next.
    /// </para>
    /// </remarks>
    public ObservableCollection<OrderRowViewModel> ShiftHistory { get; } = [];

    private Guid shiftId;
    private decimal revenue;
    public decimal Revenue { get => revenue; private set => SetProperty(ref revenue, value); }

    // ── Money through the till ────────────────────────────────────────────────────────────────
    // Gross in, gross out, and the net that is physically countable. The four Payments* figures
    // on ShiftStats have existed since the payments feature and were consumed ONLY by the CSV
    // export, so «Принято оплат» had never been on screen at all. After refunds that is
    // load-bearing: revenue falls while the CSV shows a matching "Возвращено" line, and without the
    // operator seeing the money move there is nothing on the device that reconciles the drawer.

    private decimal paymentsCash;
    public decimal PaymentsCash { get => paymentsCash; private set => SetProperty(ref paymentsCash, value); }

    private decimal paymentsCard;
    public decimal PaymentsCard { get => paymentsCard; private set => SetProperty(ref paymentsCard, value); }

    private decimal refundsCash;
    public decimal RefundsCash { get => refundsCash; private set => SetProperty(ref refundsCash, value); }

    private decimal refundsCard;
    public decimal RefundsCard { get => refundsCard; private set => SetProperty(ref refundsCard, value); }

    /// <summary>
    /// What is physically in the drawer at the end of the shift: taken in cash, less cash handed
    /// back. This is the number the manager counts, which is why the report renders it apart from
    /// its components rather than as a fourth line among them.
    /// </summary>
    /// <remarks>
    /// Derived, never read from Core: the domain exposes the two halves as
    /// <c>PaymentsCash</c>/<c>RefundsCash</c> and deliberately does not name the difference, so
    /// that no report can quietly subtract one definition of "принято" from another. Doing the
    /// subtraction here keeps that property — the subtraction only ever happens where it is shown.
    /// <para>
    /// It can legitimately go NEGATIVE within a shift (a refund taken in this shift on an order
    /// whose payment landed in the previous one), so it is rendered as an ordinary signed amount
    /// rather than clamped: a drawer that is short must not look empty.
    /// </para>
    /// </remarks>
    public decimal CashInDrawer => PaymentsCash - RefundsCash;

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

    /// <summary>
    /// Voids a closed order from the history list. The one entry point to the refund feature for a
    /// handed-over order: the orders board is fed by <c>GetActiveOrdersAsync</c>, which excludes
    /// Completed, so before this there was no row anywhere in the app with a cancel action on a
    /// finished sale.
    /// </summary>
    /// <remarks>
    /// Takes the same <see cref="OrderRowViewModel"/> the board uses and runs the same flow —
    /// confirmation, stock disposition, reason — because a void of a completed order and a void of
    /// a preparing one are the same operation with different consequences, and two flows would
    /// drift apart on exactly the wording that matters.
    /// </remarks>
    public IAsyncRelayCommand<OrderRowViewModel> CancelOrderCommand { get; }

    /// <summary>Opens a history row on its details page, where the ledger lives.</summary>
    public IAsyncRelayCommand<OrderRowViewModel> OpenDetailsCommand { get; }
}
