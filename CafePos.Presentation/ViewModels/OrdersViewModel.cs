using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

/// <summary>The three sections the board is divided into. NOT the order's status, and not its age.</summary>
/// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

public enum QueueSection
{
    /// <summary>Being made and the promised time has already passed. Look here first.</summary>
    Urgent = 0,

    /// <summary>Being made, on time, and not promised for later.</summary>
    Working = 1,

    /// <summary>The customer named a time that has not arrived yet. A preview, not work.</summary>
    Scheduled = 2
}

/// <summary>The three tabs of the orders board, in the order they appear.</summary>
/// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

public enum OrderTab
{
    Preparing,
    Ready,
    Scheduled
}

public partial class OrderRowViewModel : ObservableObject
{
    // Material icon path DATA for the three payment states, on the native 24x24 viewBox. Strings,
    // not Geometry — see PaymentGlyph for why, and PaymentStateGlyphConverter for who parses them.
    private const string PaidGlyph = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
    private const string UnpaidGlyph = "M12.5 6.9c1.78 0 2.44.85 2.5 2.1h2.21c-.07-1.72-1.12-3.3-3.21-3.81V3h-3v2.16c-.53.12-1.03.3-1.48.54l1.47 1.47c.41-.17.91-.27 1.51-.27zM5.33 4.06L4.06 5.33 7.5 8.77c0 2.08 1.56 3.21 3.91 3.91l3.51 3.51c-.34.48-1.05.91-2.42.91-2.06 0-2.87-.92-2.98-2.1h-2.2c.12 2.19 1.76 3.42 3.68 3.83V21h3v-2.15c.96-.18 1.82-.55 2.45-1.12l2.22 2.22 1.27-1.27L5.33 4.06z";
    private const string PartialGlyph = "M11 2v20c-5.07-.5-9-4.79-9-10s3.93-9.5 9-10zm2.03 0v8.99H22c-.47-4.74-4.24-8.52-8.97-8.99zm0 11.01V22c4.74-.47 8.5-4.25 8.97-8.99h-8.97z";

    public OrderRowViewModel(Order model, IAppSettings settings, DateTimeOffset now)
    {
        Model = model;
        OrderTitle = $"{settings.OrderPrefix} #{model.OrderNumber}";
        this.now = now;
    }

    /// <summary>The moment this row was judged at, passed in rather than read from a clock.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    private readonly DateTimeOffset now;

    public Order Model { get; }
    public string OrderTitle { get; }

    public string StatusText => Model.Status switch
    {
        OrderStatus.InProgress => "Готовится",
        OrderStatus.Ready => "Готов",
        OrderStatus.Cancelled => "Отменен",
        _ => "Закрыт"
    };

    /// <summary>The cancellation reason for a voided order, or empty.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string CancellationDetail =>
        Model.Status == OrderStatus.Cancelled && !string.IsNullOrWhiteSpace(Model.CancellationReason)
            ? Model.CancellationReason
            : string.Empty;

    /// <summary>Whether this card became ready and nobody has opened it since — the unread dot.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public bool IsUnseenReady => Model.IsUnseenReady(now);

    /// <summary>What a screen reader is told about the dot, since a dot alone carries no text.</summary>
    public string UnseenReadyHint => "Готов и ещё не просмотрен";

    /// <summary>Which section this order is in at the instant the board was loaded.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public QueueSection QueueSection => IsScheduled
        ? QueueSection.Scheduled
        : IsOverdue ? QueueSection.Urgent : QueueSection.Working;

    /// <summary>Section this order belongs to in the single-list layout. The orders board is one scrolling list, not two columns, because a kanban on a 411dp phone gave each card ~190dp and clipped "Подробнее" to "Подробн". Grouping keeps the visual split the board had while giving every card the full width.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string QueueSectionName => QueueSection switch
    {
        QueueSection.Urgent => "Срочные",
        QueueSection.Scheduled => "По времени",
        _ => "В работе"
    };

    /// <summary>Sort key of the section, so «Срочные» is always above «В работе».</summary>
    public int QueueSectionOrder => (int)QueueSection;

    /// <summary>True on the first card of each section, which is where the section name is drawn.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public bool ShowGroupHeader
    {
        get => showGroupHeader;
        internal set => SetProperty(ref showGroupHeader, value);
    }

    private bool showGroupHeader;

    /// <summary>── Promise, lateness and contact ───────────────────────────────────────────────────────────── THE SORT KEY AND THE LATE MARKER ARE TWO SEPARATE MEASUREMENTS, AND THEY ARE NOT MERGED. Where a card SITS answers "what should be worked on, in what order" — measured by PromisedAt, then CreatedAt. The warning marker answers "has this one passed the moment it was promised" — measured by now against PromisedAt alone, with no reference to where the card is. In a real KDS those are different timers for different reasons: the warning colour on a kitchen screen measures how long an ITEM has been cooking, which is not how late the order's promise is. Folding one into the other would mean a late order could be reordered into a calm stretch of the queue and lose the fact that it is late — which is exactly the failure this layout exists to prevent. So the marker RIDES ALONG on the card and never moves it. The «Срочные» section is where late orders live, and that is a statement about where they are — not a promotion rule applied to the other two sections.</summary>


    /// <summary>Whether the customer asked for a time that has not arrived yet — the order is a preview ticket, not work.</summary>

    public bool IsScheduled => Model.IsScheduledAt(now);

    /// <summary>Whether the promised time has passed while the order is still on the board.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public bool IsOverdue => Model.IsOverdueAt(now);

    /// <summary>Whole minutes past the promise, and never zero on a row that is actually late.</summary>
    public int OverdueMinutes => Math.Max(1, (int)Math.Round((now - Model.PromisedAt).TotalMinutes));

    /// <summary>The badge. One word, so it fits the tag — the figure is in , which is what a screen reader is handed.</summary>

    public string OverdueText => "Просрочен";

    /// <summary>The badge's accessible description, carrying the figure the tag leaves out.</summary>
    public string OverdueHint =>
        $"Обещанное время прошло {TextFormat.Plural(OverdueMinutes, "минуту", "минуты", "минут")} назад";

    /// <summary>The lateness tint: the badge's text.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public Color OverdueColor => PaletteAccess.Resolve("Danger", "DangerDark");

    /// <summary>When this order is due. The EXACT promised time, which a staff-facing screen may show: the range quoted to the customer on the cart is an estimate for a time the till picks, and this is the figure the order was actually promised and is judged against.</summary>

    public string PromisedAtText => Model.PromisedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>The promise, worded for the kind of promise it is: a time the customer named, or the lead-time one the till gave.</summary>

    public string PromiseText => IsScheduled ? $"к {PromisedAtText}" : $"обещано к {PromisedAtText}";

    /// <summary>How the order is fulfilled, in the board's own words.</summary>
    public string OrderTypeText => Model.OrderType == OrderType.Takeaway ? "С собой" : "В зале";

    /// <summary>The contact, MASKED, or empty.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string PhoneText =>
        Model.CustomerPhone is null ? string.Empty : PhoneNumber.Mask(Model.CustomerPhone);

    public string StatusHint => Model.Status switch
    {
        OrderStatus.Ready => "Ждет выдачи",
        OrderStatus.Cancelled => "Заказ отменен",
        OrderStatus.InProgress => "Заказ готовится",
        _ => "Заказ закрыт"
    };

    /// <summary>The status tint: a card border, the status label and — on the shift report — the history row's status line.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public Color StatusColor => Model.Status switch
    {
        OrderStatus.Ready => PaletteAccess.Resolve("Success", "SuccessDark"),
        OrderStatus.Cancelled => PaletteAccess.Resolve("Gray600", "Gray400"),
        OrderStatus.InProgress => PaletteAccess.Resolve("WarningText", "WarningDark"),
        _ => PaletteAccess.Resolve("Info", "InfoDark")
    };

    /// <summary>── Payment state ────────────────────────────────────────────────────────────────────────── Derived from the model the same way StatusColor is: a computed property on the row, not a converter. The pictogram binds PaymentGlyph (the mark) and PaymentColor (its fill); the visible line and the accessible description carry the wording, so the state is never told by colour alone.</summary>


    public PaymentState PaymentState => Model.PaymentState;

    /// <summary>True when the card offers to take a payment: the order is not fully paid. The board only lists active orders, so not-paid is the whole condition here.</summary>

    public bool CanCollectPayment => Model.PaymentState is not PaymentState.Paid;

    /// <summary>The path DATA of the payment pictogram, not a parsed Geometry.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string PaymentGlyph => Model.PaymentState switch
    {
        PaymentState.Paid => PaidGlyph,
        PaymentState.PartiallyPaid => PartialGlyph,
        _ => UnpaidGlyph
    };

    /// <summary>The payment tint: the pictogram's fill and the payment line's colour.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public Color PaymentColor => Model.PaymentState switch
    {
        PaymentState.Paid => PaletteAccess.Resolve("Success", "SuccessDark"),
        PaymentState.PartiallyPaid => PaletteAccess.Resolve("WarningText", "WarningDark"),
        _ => PaletteAccess.Resolve("Danger", "DangerDark")
    };

    /// <summary>The visible payment line. A partial payment says what is still owed, so it reads differently from an unpaid one.</summary>

    public string PaymentText => Model.PaymentState switch
    {
        PaymentState.Paid => "Оплачен",
        PaymentState.PartiallyPaid => $"Оплачен частично · осталось {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}",
        _ => $"Не оплачен · к оплате {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}"
    };

    /// <summary>
    /// The pictogram's accessible description: the full sentence, including what is still owed.
    /// </summary>
    public string PaymentHint => Model.PaymentState switch
    {
        PaymentState.Paid => "Заказ оплачен полностью",
        PaymentState.PartiallyPaid => $"Заказ оплачен частично, осталось {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}",
        _ => $"Заказ не оплачен, к оплате {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}"
    };

    public string NextActionText => Model.Status == OrderStatus.InProgress ? "Готов" : "Закрыть";
    public bool CanAdvance => Model.Status is OrderStatus.InProgress or OrderStatus.Ready;

    /// <summary>What the cancel button says on this card. A paid order is not just being closed off — money is going back to the customer, and the label is the only place that can say so before the operator taps.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string CancelText => Model.PaidKopecks > 0
        ? "Отменить и вернуть деньги"
        : "Отменить заказ";

    /// <summary>The pictogram-free sentence for the cancel button, carrying the amount that would go back. The button's own text says a return happens; this says how much, for a screen reader.</summary>

    public string CancelHint => Model.PaidKopecks > 0
        ? $"Отменить заказ и вернуть клиенту {TextFormat.Money(Money.FromKopecks(Model.PaidKopecks))}"
        : "Отменить заказ";
    /// <summary>Whether the cancel button is offered at all.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public bool CanCancel => Model.Status is not OrderStatus.Cancelled;
    public decimal TotalPrice => Model.TotalPrice;

    /// <summary>The total already carrying the active currency, e.g. "740,00 ₽" or "740,00 ₿".</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public string TotalPriceText => TextFormat.Money(TotalPrice);

    /// <summary>Re-raises after the currency setting changed.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(TotalPriceText));
        OnPropertyChanged(nameof(PaymentHint));
        OnPropertyChanged(nameof(PaymentLine));
    }

    /// <summary>Timestamps are stored in UTC and rendered in the local time zone.</summary>
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    /// <remarks>`docs/decisions/orders-board.md`</remarks>

    public IEnumerable<OrderItemLine> ItemLines => Model.Items.Select(item => new OrderItemLine(
        $"{item.ProductName}{(string.IsNullOrWhiteSpace(item.SelectedVariantName) ? string.Empty : $" [{item.SelectedVariantName}]")}" +
        $"{(string.IsNullOrWhiteSpace(item.SelectedModifierName) ? string.Empty : $" ({item.SelectedModifierName})")}",
        item.Quantity));
}

/// <summary>One order line, split so the name and the quantity can sit in their own columns.</summary>
/// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

public sealed record OrderItemLine(string Name, int Quantity);

public partial class OrdersViewModel : ObservableObject
{
    private readonly IOrderOperations orders;
    private readonly IAppSettings settings;
    private readonly INavigationService navigation;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly IPaymentSheet paymentSheet;
    private readonly IStockDispositionSheet stockDisposition;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrdersViewModel> logger;

    private readonly SemaphoreSlim loadGate = new(1, 1);
    private CancellationTokenSource? refreshCancellation;
    private Task? refreshTask;

    public OrdersViewModel(
        IOrderOperations orders,
        IAppSettings settings,
        INavigationService navigation,
        IDialogService dialogs,
        IHapticService haptics,
        IPaymentSheet paymentSheet,
        IStockDispositionSheet stockDisposition,
        TimeProvider timeProvider,
        ILogger<OrdersViewModel> logger)
    {
        this.orders = orders;
        this.settings = settings;
        this.navigation = navigation;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.paymentSheet = paymentSheet;
        this.stockDisposition = stockDisposition;
        this.timeProvider = timeProvider;
        this.logger = logger;

        /// <summary>AllowConcurrentExecutions: RefreshView.IsRefreshing is bound to IsBusy, so setting IsBusy = true re-triggers LoadCommand. Without AllowConcurrentExecutions the command would reject the re-entrant Execute call and the RefreshView spinner could get stuck. LoadAsync also has an `if (IsBusy) return;` guard to prevent the feedback loop from re-executing the Orders query on every cycle (infinite loading).</summary>

        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AdvanceStatusCommand = new AsyncRelayCommand<OrderRowViewModel>(AdvanceStatusAsync);
        OpenDetailsCommand = new AsyncRelayCommand<OrderRowViewModel>(OpenDetailsAsync);
        CancelOrderCommand = new AsyncRelayCommand<OrderRowViewModel>(CancelOrderAsync);
        CollectPaymentCommand = new AsyncRelayCommand<OrderRowViewModel>(CollectPaymentAsync);
        SelectTabCommand = new RelayCommand<OrderTab>(SelectTab);
    }

    /// <summary>Orders still being made, soonest arrival first, with the pre-orders pinned to the BOTTOM.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public ObservableCollection<OrderRowViewModel> PreparingOrders { get; } = [];

    /// <summary>Orders finished and waiting to be handed over, by arrival.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public ObservableCollection<OrderRowViewModel> ReadyOrders { get; } = [];

    /// <summary>Pre-orders with a time still in the future, soonest promise first.</summary>
    public ObservableCollection<OrderRowViewModel> ScheduledOrders { get; } = [];

    /// <summary>Tab captions with their counts. The count is on the tab rather than inside it because the reason to look is arithmetic — is anything waiting — and reading that off a tab bar costs nothing, while discovering it by opening the tab costs the operator their place in the queue.</summary>

    public string PreparingTabText => $"Готовятся · {PreparingOrders.Count}";
    public string ReadyTabText => $"Ждут выдачи · {ReadyOrders.Count}";
    public string ScheduledTabText => $"По времени · {ScheduledOrders.Count}";

    /// <summary>Whether any finished order is still unopened — the dot on the «Ждут выдачи» chip.</summary>
    /// <remarks>Почему так — `docs/decisions/orders-board.md`</remarks>

    public bool HasUnseenReady
    {
        get
        {
            foreach (var row in ReadyOrders)
            {
                if (row.IsUnseenReady) return true;
            }

            return false;
        }
    }

    /// <summary>Why each tab is empty, so a zero is a sentence and not a blank screen.</summary>
    public string PreparingEmptyText => "Готовящихся заказов нет.";
    public string ReadyEmptyText => "Ничего не ждёт выдачи.";
    public string ScheduledEmptyText => "Предзаказов на будущее нет.";

    /// <summary>Which tab is showing. is the default, and that is the owner's instruction: for a single operator the queue is worked from what is being made, and the count on «Ждут выдачи» says whether anything has finished without anyone having to look into the tab.</summary>

    private OrderTab selectedTab = OrderTab.Preparing;
    public OrderTab SelectedTab
    {
        get => selectedTab;
        private set
        {
            if (!SetProperty(ref selectedTab, value)) return;
            OnPropertyChanged(nameof(IsPreparingTab));
            OnPropertyChanged(nameof(IsReadyTab));
            OnPropertyChanged(nameof(IsScheduledTab));
        }
    }

    public bool IsPreparingTab => SelectedTab == OrderTab.Preparing;
    public bool IsReadyTab => SelectedTab == OrderTab.Ready;
    public bool IsScheduledTab => SelectedTab == OrderTab.Scheduled;

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> AdvanceStatusCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> OpenDetailsCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> CancelOrderCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> CollectPaymentCommand { get; }

    /// <summary>Switches the visible tab. Takes the <see cref="OrderTab"/>, bound through x:Static.</summary>
    public IRelayCommand<OrderTab> SelectTabCommand { get; }
}
