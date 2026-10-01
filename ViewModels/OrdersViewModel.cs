using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

public partial class OrderRowViewModel : ObservableObject
{
    // Material icon geometry for the three payment states, on the native 24x24 viewBox. Parsed
    // once with the same converter XAML uses for Path="M …" (Geometry has no Parse method).
    private static readonly Geometry PaidGlyph = ParseGeometry("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z");
    private static readonly Geometry UnpaidGlyph = ParseGeometry("M12.5 6.9c1.78 0 2.44.85 2.5 2.1h2.21c-.07-1.72-1.12-3.3-3.21-3.81V3h-3v2.16c-.53.12-1.03.3-1.48.54l1.47 1.47c.41-.17.91-.27 1.51-.27zM5.33 4.06L4.06 5.33 7.5 8.77c0 2.08 1.56 3.21 3.91 3.91l3.51 3.51c-.34.48-1.05.91-2.42.91-2.06 0-2.87-.92-2.98-2.1h-2.2c.12 2.19 1.76 3.42 3.68 3.83V21h3v-2.15c.96-.18 1.82-.55 2.45-1.12l2.22 2.22 1.27-1.27L5.33 4.06z");
    private static readonly Geometry PartialGlyph = ParseGeometry("M11 2v20c-5.07-.5-9-4.79-9-10s3.93-9.5 9-10zm2.03 0v8.99H22c-.47-4.74-4.24-8.52-8.97-8.99zm0 11.01V22c4.74-.47 8.5-4.25 8.97-8.99h-8.97z");

    public OrderRowViewModel(Order model, AppSettings settings)
    {
        Model = model;
        OrderTitle = $"{settings.OrderPrefix} #{model.OrderNumber}";
    }

    private static Geometry ParseGeometry(string path) =>
        (Geometry)new PathGeometryConverter().ConvertFromString(path)!;

    public Order Model { get; }
    public string OrderTitle { get; }

    public string StatusText => Model.Status switch
    {
        OrderStatus.InProgress => "Готовится",
        OrderStatus.Ready => "Готов",
        OrderStatus.Cancelled => "Отменен",
        _ => "Закрыт"
    };

    /// <summary>
    /// The cancellation reason for a voided order, or empty.
    /// </summary>
    /// <remarks>
    /// A separate property rather than appended to <see cref="StatusText"/>, because the shift
    /// report's history row shows the composed reason underneath the status while the orders board
    /// does not need it at all: the board never lists a cancelled order, since
    /// <c>GetActiveOrdersAsync</c> excludes both terminal statuses.
    /// <para>
    /// Worth having on the history row because the domain composes the whole story into this one
    /// string — how much was refunded, whether stock came back, and which ingredients could not be
    /// reversed. A voided sale with only "Отменен" on it tells a manager the order is gone and
    /// nothing about where the money went.
    /// </para>
    /// </remarks>
    public string CancellationDetail =>
        Model.Status == OrderStatus.Cancelled && !string.IsNullOrWhiteSpace(Model.CancellationReason)
            ? Model.CancellationReason
            : string.Empty;

    /// <summary>
    /// Section this order belongs to in the single-list layout. The orders board is one
    /// scrolling list, not two columns, because a kanban on a 411dp phone gave each card
    /// ~190dp and clipped "Подробнее" to "Подробн". Grouping keeps the visual split the
    /// board had while giving every card the full width.
    /// </summary>
    /// <remarks>
    /// A string, not an enum, because <c>PropertyGroupDescription</c> binds the group header
    /// template to this exact value — the header is the property, so it has to read as
    /// Russian prose. Column order then follows the order the rows first appear in, which
    /// is why LoadAsync sorts preparing ahead of ready.
    /// </remarks>
    public string StatusGroupName => Model.Status == OrderStatus.Ready ? "Ждут выдачи" : "Готовятся";

    /// <summary>
    /// True on the first card of each section, which is where the section name is drawn.
    /// </summary>
    /// <remarks>
    /// Set once the rows are sorted preparing-first, not from the constructor — a row cannot
    /// know whether an earlier row in the same group exists. The name itself is text in a
    /// heading, so the two sections are told apart by their wording and never by colour alone.
    /// <para>
    /// This raises change notification, which a plain auto-property does not. Switching the
    /// section filter rewrites the flag on rows that are already in the list and leaves the
    /// collection itself structurally unchanged, so CollectionView recycles the cells instead of
    /// re-inflating the templates. Without the notification the heading stayed on screen after
    /// its section had been filtered to a single chip that already names it.
    /// </para>
    /// </remarks>
    public bool ShowGroupHeader
    {
        get => showGroupHeader;
        internal set => SetProperty(ref showGroupHeader, value);
    }

    private bool showGroupHeader;

    public string StatusHint => Model.Status switch
    {
        OrderStatus.Ready => "Ждет выдачи",
        OrderStatus.Cancelled => "Заказ отменен",
        OrderStatus.InProgress => "Заказ готовится",
        _ => "Заказ закрыт"
    };

    public Color StatusColor => Model.Status switch
    {
        OrderStatus.Ready => Colors.Green,
        OrderStatus.Cancelled => Colors.Gray,
        OrderStatus.InProgress => Colors.Orange,
        _ => Colors.SteelBlue
    };

    // ── Payment state ──────────────────────────────────────────────────────────────────────────
    // Derived from the model the same way StatusColor is: a computed property on the row, not a
    // converter. The pictogram binds PaymentGlyph (the mark) and PaymentColor (its fill); the
    // visible line and the accessible description carry the wording, so the state is never told
    // by colour alone.

    public PaymentState PaymentState => Model.PaymentState;

    /// <summary>
    /// True when the card offers to take a payment: the order is not fully paid. The board only
    /// lists active orders, so not-paid is the whole condition here.
    /// </summary>
    public bool CanCollectPayment => Model.PaymentState is not PaymentState.Paid;

    public Geometry PaymentGlyph => Model.PaymentState switch
    {
        PaymentState.Paid => PaidGlyph,
        PaymentState.PartiallyPaid => PartialGlyph,
        _ => UnpaidGlyph
    };

    public Color PaymentColor => Model.PaymentState switch
    {
        PaymentState.Paid => Colors.Green,
        PaymentState.PartiallyPaid => Colors.Orange,
        _ => Colors.Red
    };

    /// <summary>
    /// The visible payment line. A partial payment says what is still owed, so it reads
    /// differently from an unpaid one.
    /// </summary>
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

    /// <summary>
    /// What the cancel button says on this card. A paid order is not just being closed off — money
    /// is going back to the customer, and the label is the only place that can say so before the
    /// operator taps.
    /// </summary>
    /// <remarks>
    /// Written out per row rather than put in a resource, because the two variants differ and a
    /// resource lookup cannot branch. It reads in the imperative so it fits the buttons it sits
    /// between ("Подробнее" / "Готов"), which are also per-row wording.
    /// </remarks>
    public string CancelText => Model.PaidKopecks > 0
        ? "Отменить и вернуть деньги"
        : "Отменить заказ";

    /// <summary>
    /// The pictogram-free sentence for the cancel button, carrying the amount that would go back.
    /// The button's own text says a return happens; this says how much, for a screen reader.
    /// </summary>
    public string CancelHint => Model.PaidKopecks > 0
        ? $"Отменить заказ и вернуть клиенту {TextFormat.Money(Money.FromKopecks(Model.PaidKopecks))}"
        : "Отменить заказ";
    /// <summary>
    /// Whether the cancel button is offered at all.
    /// </summary>
    /// <remarks>
    /// Open AND closed orders alike, paid or not. A paid order is cancellable now: cancelling
    /// voids the sale AND refunds the whole collected amount in the same transaction, so the cash
    /// goes back to the customer and the order leaves the revenue — which is the only correct
    /// outcome for a sale that should never have happened at all.
    /// <para>
    /// This used to carry <c>!Model.IsFullyPaid</c>, which was a UI workaround for a domain guard
    /// that has since been removed along with the guard itself. The comment it replaced is worth
    /// keeping in mind, because its reasoning was sound for what the domain did then: with no way
    /// to put money back, a cancel button on a paid order was a control that could only fail, and a
    /// failed cancel cost three steps (tap, confirm, reason) to reach "Нельзя отменить оплаченный
    /// заказ". It was also a hole around the payment block — cancelling was one tap and paying was
    /// three, so under queue pressure cancel was the tempting one, and the sale silently vanished
    /// from the shift report while the cash stayed in the drawer. Cancelling a paid order is now a
    /// working, money-moving operation, so neither cost applies.
    /// </para>
    /// <para>
    /// Note that <see cref="Order.IsFullyPaid"/> was never the same predicate as the domain guard
    /// it shadowed, which was <c>PaidKopecks &gt; 0</c>. A partially paid order has
    /// <c>IsFullyPaid == false</c> and so passed the UI, then failed in the domain — the button
    /// appeared and cancelled nothing. That inconsistency is gone with the guard.
    /// </para>
    /// </remarks>
    public bool CanCancel => Model.Status is not OrderStatus.Cancelled;
    public decimal TotalPrice => Model.TotalPrice;

    /// <summary>Timestamps are stored in UTC and rendered in the local time zone.</summary>
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public IEnumerable<OrderItemLine> ItemLines => Model.Items.Select(item => new OrderItemLine(
        $"{item.ProductName}{(string.IsNullOrWhiteSpace(item.SelectedVariantName) ? string.Empty : $" [{item.SelectedVariantName}]")}" +
        $"{(string.IsNullOrWhiteSpace(item.SelectedModifierName) ? string.Empty : $" ({item.SelectedModifierName})")}",
        item.Quantity));
}

/// <summary>
/// One order line, split so the name and the quantity can sit in their own columns.
/// </summary>
/// <remarks>
/// These were a single preformatted string, so a long name pushed "× 1" onto a second line
/// and it landed alone under the name — measured on the emulator at 158dp, where
/// "Капучино (Овсяное) × 1" wrapped and orphaned its quantity. A trailing quantity is a
/// column of its own, never a wrap casualty.
/// </remarks>
public sealed record OrderItemLine(string Name, int Quantity);

public partial class OrdersViewModel : ObservableObject
{
    private readonly IOrderService orders;
    private readonly AppSettings settings;
    private readonly INavigationService navigation;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly IPaymentSheet paymentSheet;
    private readonly IStockDispositionSheet stockDisposition;
    private readonly ILogger<OrdersViewModel> logger;

    private readonly SemaphoreSlim loadGate = new(1, 1);
    private CancellationTokenSource? refreshCancellation;
    private Task? refreshTask;

    public OrdersViewModel(
        IOrderService orders,
        AppSettings settings,
        INavigationService navigation,
        IDialogService dialogs,
        IHapticService haptics,
        IPaymentSheet paymentSheet,
        IStockDispositionSheet stockDisposition,
        ILogger<OrdersViewModel> logger)
    {
        this.orders = orders;
        this.settings = settings;
        this.navigation = navigation;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.paymentSheet = paymentSheet;
        this.stockDisposition = stockDisposition;
        this.logger = logger;

        // AllowConcurrentExecutions: RefreshView.IsRefreshing is bound to IsBusy, so setting
        // IsBusy = true re-triggers LoadCommand. Without AllowConcurrentExecutions the command
        // would reject the re-entrant Execute call and the RefreshView spinner could get stuck.
        // LoadAsync also has an `if (IsBusy) return;` guard to prevent the feedback loop from
        // re-executing the Orders query on every cycle (infinite loading).
        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AdvanceStatusCommand = new AsyncRelayCommand<OrderRowViewModel>(AdvanceStatusAsync);
        OpenDetailsCommand = new AsyncRelayCommand<OrderRowViewModel>(OpenDetailsAsync);
        CancelOrderCommand = new AsyncRelayCommand<OrderRowViewModel>(CancelOrderAsync);
        CollectPaymentCommand = new AsyncRelayCommand<OrderRowViewModel>(CollectPaymentAsync);
        SelectSectionFilterCommand = new RelayCommand<OrderFilterChip>(SelectSectionFilter);
    }

    /// <summary>Every active order, sorted preparing first. The source of truth for the board.</summary>
    public ObservableCollection<OrderRowViewModel> ActiveOrders { get; } = [];

    /// <summary>
    /// The orders the list actually shows: <see cref="ActiveOrders"/> narrowed by
    /// <see cref="ActiveFilter"/>. Kept separate from the source collection so changing the filter
    /// is a local re-projection — the rows that are filtered out stay in
    /// <see cref="ActiveOrders"/> instead of being dropped from it, and switching back is
    /// instant with no query.
    /// </summary>
    public ObservableCollection<OrderRowViewModel> VisibleOrders { get; } = [];

    public ObservableCollection<OrderFilterChip> SectionFilters { get; } = [];

    private OrderSectionFilter activeFilter = OrderSectionFilter.All;
    public OrderSectionFilter ActiveFilter
    {
        get => activeFilter;
        private set => SetProperty(ref activeFilter, value);
    }

    /// <summary>
    /// What the list says when it is empty. Two different situations, so two different sentences:
    /// a board with nothing on it is not the same as a filter that happens to be hiding something.
    /// </summary>
    public string EmptyListText => ActiveOrders.Count == 0
        ? "Активных заказов нет."
        : "В этом разделе заказов нет.";

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
    public IRelayCommand<OrderFilterChip> SelectSectionFilterCommand { get; }
}
