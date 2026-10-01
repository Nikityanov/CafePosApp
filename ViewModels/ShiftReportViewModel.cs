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
    /// <summary>
    /// Mirror of the domain's reason length, used only to refuse an over-long reason BEFORE it is
    /// thrown away. See the remark at the check itself.
    /// </summary>
    /// <remarks>
    /// A copy, and deliberately so: <c>OrderService.MaxDiscrepancyReasonLength</c> is private, and
    /// making it public would put a presentation concern into the domain's API. The rule that counts
    /// is the domain's — this one only decides whether the operator keeps their text.
    /// </remarks>
    private const int MaxDiscrepancyReasonLength = 300;

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
    /// It CANNOT go negative. An earlier version of this comment claimed the opposite — "a refund
    /// taken in this shift on an order whose payment landed in the previous one" — and that reasoning
    /// was false: attribution follows the ORDER's shift, not the shift in which the refund button was
    /// pressed. <c>ShiftPayments</c> joins the ledger to Orders and filters on <c>order.ShiftId</c>
    /// with no timestamp filter, and <c>PaymentRecorder</c> guarantees per-order per-method
    /// <c>refunded &lt;= collected</c>; summed over a shift's own orders that is the same inequality
    /// for the shift. So the drawer figure is <c>&gt;= 0</c> always.
    /// <para>
    /// The claim was harmless while this number was only rendered, and stops being harmless the moment
    /// a stored snapshot is compared against it — a reader who believed it would expect a negative
    /// drawer figure to be a legitimate, unremarkable outcome rather than a bug.
    /// </para>
    /// <para>
    /// SOURCED, NOT COMPUTED. This used to be <c>PaymentsCash - RefundsCash</c> written out here,
    /// and it is now a straight copy of <c>ShiftStats.ExpectedCashNow</c>. The local arithmetic was
    /// deleted rather than kept as a convenience because it was a SECOND definition of the same
    /// figure, and a second definition is exactly what the remark above warns about one level up:
    /// once the close stores <c>Counted − Expected</c> and compares it against what the screen says,
    /// two definitions are two answers, and the operator is asked to arbitrate between them. The
    /// arithmetic now lives once, in the service, where both halves already exist — which is also
    /// why the hand-written <c>OnPropertyChanged(nameof(CashInDrawer))</c> in LoadAsync is gone:
    /// the property is fed, not derived, so SetProperty raises the change itself.
    /// </para>
    /// </remarks>
    private decimal cashInDrawer;
    public decimal CashInDrawer { get => cashInDrawer; private set => SetProperty(ref cashInDrawer, value); }

    /// <summary>
    /// The last amount the operator typed into the cash-count prompt, kept so a failed close does
    /// not cost them the count.
    /// </summary>
    /// <remarks>
    /// The drawer is emptied BEFORE the close is attempted, so by the time the domain refuses —
    /// orders still open, most often — the money the operator just counted is in no drawer at all
    /// and no longer on screen. Making them re-type "4 320,00" to satisfy an unrelated guard is how
    /// a shift ends up closed with a count of 0, which reads as "the till was robbed" and is the
    /// single most damaging value this feature can store. The retry pre-fills the previous entry
    /// instead, so the second attempt is one tap.
    /// <para>
    /// Null means "nothing typed yet", which is NOT the same as a typed 0: a count of 0 is a real
    /// count and the pre-fill for it is "0", not the live figure.
    /// </para>
    /// </remarks>
    private long? lastCountedCashKopecks;

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
