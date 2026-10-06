using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

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
    private readonly IAppSettings settings;
    private readonly IStockDispositionSheet stockDisposition;
    private readonly IHapticService haptics;
    private readonly ICashLedgerService cashLedger;
    private readonly IShiftSession shiftSession;
    private readonly ILogger<ShiftReportViewModel> logger;

    public ShiftReportViewModel(
        IOrderService orders,
        IReportExportService reports,
        IBackupService backups,
        IFileService files,
        IDialogService dialogs,
        INavigationService navigation,
        IAppSettings settings,
        IStockDispositionSheet stockDisposition,
        IHapticService haptics,
        ICashLedgerService cashLedger,
        IShiftSession shiftSession,
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
        this.cashLedger = cashLedger;
        this.shiftSession = shiftSession;

        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        OpenAnalyticsCommand = new AsyncRelayCommand(() => navigation.GoToTabAsync("shift-analytics"));
        CloseShiftCommand = new AsyncRelayCommand(CloseShiftAsync);
        ExportShiftCommand = new AsyncRelayCommand(ExportShiftAsync);
        CancelOrderCommand = new AsyncRelayCommand<OrderRowViewModel>(CancelOrderAsync);
        OpenDetailsCommand = new AsyncRelayCommand<OrderRowViewModel>(row =>
            row is null ? Task.CompletedTask : navigation.GoToOrderDetailsAsync(row.Model.Id));
        AddFloatCommand = new AsyncRelayCommand(AddFloatAsync);
        AddPayoutCommand = new AsyncRelayCommand(AddPayoutAsync);
        CancelMovementCommand = new AsyncRelayCommand<CashMovementRow>(CancelMovementAsync);
        OpenShiftCommand = new AsyncRelayCommand(() => navigation.GoToOpenShiftAsync());
    }

    /// <summary>
    /// The change put in and the cash carried out, oldest first — the drawer as a list of events
    /// rather than a single number.
    /// </summary>
    /// <remarks>
    /// Present because a drawer figure with nothing behind it cannot be investigated. A shortage is a
    /// single number, and this is the list that says whether it is a missed collection, a mistyped
    /// amount or change that was never counted in — which are three different conversations with the
    /// operator and three different fixes.
    /// <para>
    /// Corrections are rows here too, not edits: the operator can see that something happened AND that
    /// it was undone, which is the fact the ledger exists to preserve.
    /// </para>
    /// </remarks>
    public ObservableCollection<CashMovementRow> CashMovements { get; } = [];

    /// <summary>
    /// BindableLayout has no EmptyView, so «Движений по кассе не было.» is a label bound here — the
    /// same arrangement as <see cref="HasNoShiftHistory"/>, and for the same reason.
    /// </summary>
    public bool HasNoCashMovements => CashMovements.Count == 0;

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

    /// <summary>
    /// True while the shift has no closed orders at all.
    /// </summary>
    /// <remarks>
    /// The list is a BindableLayout inside the page's ScrollView, not a CollectionView on a star
    /// row, so «Закрытых заказов пока нет.» has no <c>EmptyView</c> to live in — BindableLayout has
    /// none. An ordinary label bound to this is what carries it, and it is the reason this property
    /// exists at all. Re-announced from <c>LoadAsync</c> after every <c>SyncWith</c>, which is the
    /// only place <see cref="ShiftHistory"/> changes: SyncWith edits the collection without knowing
    /// this property exists, so nothing about a reload would re-raise it on its own.
    /// </remarks>
    public bool HasNoShiftHistory => ShiftHistory.Count == 0;

    // ── Price control: «Скидки» ───────────────────────────────────────────────────────────────
    // The shift's lines whose charged price differed from the price they were allowed to be sold
    // at. This section IS the price-control feature (plan 4.2), and it is deliberately detection
    // only: nothing is refused at the till, no reason code is collected and no PIN is asked for —
    // research is explicit that a justification captured at the till becomes the first option
    // clicked and controls nothing, and there is no operator entity in this project to hold an
    // approval. A manager reads this after the shift instead, at zero friction during the sale.

    /// <summary>
    /// The mismatched lines, VOIDED ORDERS INCLUDED. A voided sale is exactly where an overridden
    /// price is worth seeing, and it is absent from the revenue precisely because the money went
    /// back — which is why each row carries its status and why none of this may be added up.
    /// </summary>
    public ObservableCollection<DiscountedLineRow> DiscountedLines { get; } = [];

    /// <summary>BindableLayout has no EmptyView, so the empty state is a label bound to this.</summary>
    public bool HasNoDiscountedLines => DiscountedLines.Count == 0;

    /// <summary>
    /// What the section is and is not, in the operator's own terms: it is a list of lines, it is not
    /// part of the revenue above it, and the voided rows in it are not part of it either.
    /// </summary>
    /// <remarks>
    /// Spelled out on the screen rather than left to be inferred from where the section sits. A
    /// report that adds a real sale's discount to a voided one's reports money nobody kept, and a
    /// section under «Выручка» with no wording on it invites exactly that reading.
    /// <para>
    /// What this section deliberately does NOT contain is documented on
    /// <see cref="IOrderService.GetDiscountedLinesAsync"/> rather than here: a bundle that is
    /// correctly priced but deliberately cheaper than its parts, because the filter is the price
    /// mismatch and that is the question this section answers.
    /// </para>
    /// </remarks>
    public string DiscountsNote =>
        "Строки, где взимаемая цена отличалась от разрешённой. В выручку они не входят и здесь не "
        + "суммируются: у отменённых заказов деньги вернулись.";

    private Guid shiftId;
    private decimal revenue;
    public decimal Revenue { get => revenue; private set => SetProperty(ref revenue, value); }

    // ── Formatted money ───────────────────────────────────────────────────────────────────────
    // These six are the report's headline figures, and each was bound as
    // StringFormat="Выручка: {0:F2} ₽" — a format string fixed at XAML parse time, so it could
    // only ever print a ruble sign with two decimals. Formatting moves into the ViewModel so it
    // can read the operator's selected currency; the Russian label stays in the markup as a plain
    // {0} substitution.
    //
    // One NotifyMoneyTexts() re-raises all of them rather than six separate pairs: every figure is
    // set in the same LoadAsync pass, so there is no case where one is fresh and another stale.

    /// <summary>Revenue in the active currency, e.g. "12 480,00 ₿".</summary>
    public string RevenueText => TextFormat.Money(Revenue);

    /// <summary>Average check in the active currency.</summary>
    public string AverageCheckText => TextFormat.Money(AverageCheck);

    /// <summary>Cash taken in the shift, in the active currency.</summary>
    public string PaymentsCashText => TextFormat.Money(PaymentsCash);

    /// <summary>Card payments taken in the shift, in the active currency.</summary>
    public string PaymentsCardText => TextFormat.Money(PaymentsCard);

    /// <summary>Cash refunded in the shift, in the active currency.</summary>
    public string RefundsCashText => TextFormat.Money(RefundsCash);

    /// <summary>Card refunds in the shift, in the active currency.</summary>
    public string RefundsCardText => TextFormat.Money(RefundsCard);

    /// <summary>What is physically in the drawer, in the active currency.</summary>
    public string CashInDrawerText => TextFormat.Money(CashInDrawer);

    /// <summary>
    /// Re-raises every formatted money figure. Called once at the end of a load, after all six
    /// decimals have been assigned.
    /// </summary>
    public void NotifyMoneyTexts()
    {
        OnPropertyChanged(nameof(RevenueText));
        OnPropertyChanged(nameof(AverageCheckText));
        OnPropertyChanged(nameof(PaymentsCashText));
        OnPropertyChanged(nameof(PaymentsCardText));
        OnPropertyChanged(nameof(RefundsCashText));
        OnPropertyChanged(nameof(RefundsCardText));
        OnPropertyChanged(nameof(CashInDrawerText));
        OnPropertyChanged(nameof(FloatCashText));
        OnPropertyChanged(nameof(PayoutCashText));
    }

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

    private decimal floatCash;
    public decimal FloatCash { get => floatCash; private set => SetProperty(ref floatCash, value); }

    private decimal payoutCash;
    public decimal PayoutCash { get => payoutCash; private set => SetProperty(ref payoutCash, value); }

    /// <summary>Change put into the drawer, in the active currency.</summary>
    public string FloatCashText => TextFormat.Money(FloatCash);

    /// <summary>Cash carried out for collection, in the active currency.</summary>
    public string PayoutCashText => TextFormat.Money(PayoutCash);

    /// <summary>
    /// True when a shift is open. False is a state the operator can arrive at on purpose — from the
    /// opening screen's «Смена» tab, or right after closing one — and the page says so instead of
    /// showing an empty report that looks like a shift with nothing in it.
    /// </summary>
    public bool HasOpenShift { get; private set; }

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
    /// <para>
    /// The EXPECTED figure is stored with the count, and the two are one field so that neither can
    /// be written without the other. The pre-fill is only honest while the drawer still holds what it
    /// held when the operator counted it, and after a refused close the drawer can and does move: two
    /// refunds taken while the operator went and closed those orders changed the figure from 6 660 to
    /// 5 580, and the dialog went on offering 6 660 in a field labelled «по учёту 5 580». One tap on
    /// «Закрыть смену» would then have stored a 1 080 discrepancy nobody counted — on the one figure
    /// this feature exists to get right, and written to the shift as fact.
    /// </para>
    /// <para>
    /// Which entry may be offered back is <c>CashCountPrefill.Resolve</c> in the core, not a line here:
    /// it is a rule about the drawer, it has no UI in it, and until it was extracted the only thing
    /// checking it was a person counting cash on a device.
    /// </para>
    /// </remarks>
    private PendingCashCount? pendingCount;

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

    /// <summary>Puts change into the drawer. Same operation as opening a shift with a float.</summary>
    public IAsyncRelayCommand AddFloatCommand { get; }

    /// <summary>Takes cash out of the drawer for collection. Refused above what the drawer holds.</summary>
    public IAsyncRelayCommand AddPayoutCommand { get; }

    /// <summary>Cancels one movement with a second, opposite row. Never edits the first.</summary>
    public IAsyncRelayCommand<CashMovementRow> CancelMovementCommand { get; }

    /// <summary>Goes to the opening screen. The only way out of this page with no shift open.</summary>
    public IAsyncRelayCommand OpenShiftCommand { get; }

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
