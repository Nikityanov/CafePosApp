using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

/// <summary>The order card: its lines, its payments, and everything that can be done to it.</summary>
public partial class OrderDetailsViewModel : ObservableObject
{
    private readonly IOrderOperations orders;
    private readonly ICatalogService catalog;
    private readonly IComboService combos;
    private readonly IComboEditor comboEditor;
    private readonly IModifierPicker modifierPicker;
    private readonly IAppSettings settings;
    private readonly INavigationService navigation;
    private readonly IPaymentSheet paymentSheet;

    /// <summary>The «Дописать» sheet. Behind a seam so the command is testable without a window.</summary>
    private readonly IContactDetailsSheet contactDetailsSheet;
    private readonly IStockDispositionSheet stockDisposition;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderDetailsViewModel> logger;

    private Guid orderId;
    private Order? order;

    public OrderDetailsViewModel(
        IOrderOperations orders,
        ICatalogService catalog,
        IComboService combos,
        IComboEditor comboEditor,
        IModifierPicker modifierPicker,
        IAppSettings settings,
        INavigationService navigation,
        IPaymentSheet paymentSheet,
        IContactDetailsSheet contactDetailsSheet,
        IStockDispositionSheet stockDisposition,
        IDialogService dialogs,
        IHapticService haptics,
        TimeProvider timeProvider,
        ILogger<OrderDetailsViewModel> logger
    )
    {
        this.orders = orders;
        this.catalog = catalog;
        this.combos = combos;
        this.comboEditor = comboEditor;
        this.modifierPicker = modifierPicker;
        this.settings = settings;
        this.navigation = navigation;
        this.paymentSheet = paymentSheet;
        this.contactDetailsSheet = contactDetailsSheet;
        this.stockDisposition = stockDisposition;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.timeProvider = timeProvider;
        this.logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddContactDetailsCommand = new AsyncRelayCommand(AddContactDetailsAsync);
        AddProductCommand = new AsyncRelayCommand(AddProductAsync);
        IncreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(IncreaseItem);
        DecreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(DecreaseItem);
        RemoveItemCommand = new RelayCommand<OrderEditItemViewModel>(RemoveItem);
        /// <summary>CanExecute = CanEdit on the two COMMANDS the gesture recognizers use.</summary>
        /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

        EditItemPriceCommand = new AsyncRelayCommand<OrderEditItemViewModel>(EditItemPriceAsync, canExecute: _ => CanEdit);
        EditItemCompositionCommand = new AsyncRelayCommand<OrderEditItemViewModel>(EditItemCompositionAsync, canExecute: _ => CanEdit);
        ToggleFulfilmentCommand = new RelayCommand(() => IsFulfilmentExpanded = !IsFulfilmentExpanded);
        CollectPaymentCommand = new AsyncRelayCommand(CollectPaymentAsync);
        RefundPaymentCommand = new AsyncRelayCommand(RefundPaymentAsync);
        CancelOrderCommand = new AsyncRelayCommand(CancelOrderAsync);
        BackCommand = new AsyncRelayCommand(navigation.GoBackAsync);
    }

    public ObservableCollection<OrderEditItemViewModel> Items { get; } = [];
    public ObservableCollection<Product> AvailableProducts { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<PaymentLine> Payments { get; } = [];

    private Product? selectedProduct;
    public Product? SelectedProduct { get => selectedProduct; set => SetProperty(ref selectedProduct, value); }

    public string OrderTitle => order is null ? "Детали заказа" : $"{settings.OrderPrefix} #{order.OrderNumber}";

    public string StatusText => order?.Status switch
    {
        OrderStatus.InProgress => "Готовится",
        OrderStatus.Ready => "Готов",
        OrderStatus.Completed => "Закрыт",
        OrderStatus.Cancelled => $"Отменен{(string.IsNullOrWhiteSpace(order.CancellationReason) ? string.Empty : $": {order.CancellationReason}")}",
        _ => string.Empty
    };

    public bool CanEdit => order?.Status == OrderStatus.InProgress;
    public decimal Total => Items.Sum(item => item.LineTotal);

    /// <summary>── Fulfilment, contact and promise ────────────────────────────────────────────────────────── READ-ONLY HERE, AND THAT IS A KNOWN LIMITATION RATHER THAN A CHOIC...</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>


    /// <summary>Whether the fulfilment/contact/promise card is worth showing at all.</summary>
    public bool HasOrderDetails => order is not null;

    /// <summary>── The same disclosure the cart uses, for the same reason ─────────────────────────────────── MenuPage collapsed its fulfilment block after it measured 153dp of...</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>


    private bool isFulfilmentExpanded;

    public bool IsFulfilmentExpanded
    {
        get => isFulfilmentExpanded;
        set
        {
            // FulfilmentSummary is raised with it — the row's caption depends on whether the block is
            // open, and leaving it stale would print the state twice or not at all.
            if (SetProperty(ref isFulfilmentExpanded, value))
            {
                OnPropertyChanged(nameof(FulfilmentToggleHint));
                OnPropertyChanged(nameof(FulfilmentSummary));
            }
        }
    }

    /// <remarks>`docs/decisions/order-details.md`</remarks>

    public string FulfilmentSummary => order is null
        ? string.Empty
        : IsFulfilmentExpanded
            ? FulfilmentRowTitle
            : $"{OrderTypeText} · готово к {WhenText}";

    /// <summary>The neutral heading the row falls back to while the block is open.</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

    public const string FulfilmentRowTitle = "Параметры заказа";

    /// <summary>The promised clock time, unqualified — see <see cref="FulfilmentSummary"/>.</summary>
    private string WhenText => order!.RequestedAt is { } requested
        ? requested.ToLocalTime().ToString("HH:mm")
        : order.PromisedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>What the row announces and what pressing it does; the chevron itself carries no text.</summary>
    public string FulfilmentToggleHint =>
        IsFulfilmentExpanded
            ? $"{FulfilmentRowTitle}. Свернуть телефон и время заказа."
            : $"{FulfilmentSummary}. Показать телефон и время заказа.";

    /// <summary>How the order is fulfilled, in the page's own words.</summary>
    public string OrderTypeText => order is null
        ? string.Empty
        : order.OrderType == OrderType.Takeaway ? "С собой" : "В зале";

    /// <summary>The contact in FULL, or an empty string.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public string CustomerPhoneText => order?.CustomerPhone ?? string.Empty;

    /// <summary>Whether the order carries a contact worth printing on the block.</summary>
    public bool HasCustomerPhone => !string.IsNullOrWhiteSpace(CustomerPhoneText);

    /// <summary>Whether a phone is expected at all — takeaway only.</summary>
    public bool ExpectsPhone => order?.OrderType == OrderType.Takeaway;

    /// <summary>A counter-service order, where no phone was asked for and none is stored.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public bool ExpectsNoPhone => order is not null && !ExpectsPhone;

    /// <summary>The promise, worded for what kind of promise it is.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public string PromiseText => order is null
        ? string.Empty
        : order.RequestedAt is { } requested
            ? $"к {requested.ToLocalTime():HH:mm}"
            : $"обещано к {order.PromisedAt.ToLocalTime():HH:mm}";

    /// <summary>Whether the promise has already passed while the order is still open.</summary>
    public bool IsOverdue => order is not null && order.IsOverdueAt(timeProvider.GetUtcNow());

    /// <summary>Whole minutes past the promise, or zero when there is no lateness to report.</summary>
    public int OverdueMinutes => order is null
        ? 0
        : Math.Max(0, (int)Math.Round((timeProvider.GetUtcNow() - order.PromisedAt).TotalMinutes));

    /// <summary>The lateness line, or empty. Names the state in words; the colour reinforces it.</summary>
    public string OverdueText => IsOverdue
        ? $"Просрочен на {TextFormat.Plural(OverdueMinutes, "минуту", "минуты", "минут")}"
        : string.Empty;

    /// <summary>The lateness tint for <see cref="OverdueText"/>.</summary>
    public Color OverdueColor => PaletteAccess.Resolve("Danger", "DangerDark");

    /// <remarks>`docs/decisions/order-details.md`</remarks>

    public string TotalText => TextFormat.Money(Total);

    /// <summary>── Payment state ────────────────────────────────────────────────────────────────────────── The order's own payment figures (PaidKopecks / BalanceKopecks / Paym...</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>


    /// <remarks>`docs/decisions/order-details.md`</remarks>

    public bool CanCollectPayment =>
        order is not null
        && order.PaymentState is not PaymentState.Paid
        && order.Status is OrderStatus.InProgress or OrderStatus.Ready;

    /// <summary>True when this order can have money returned: finished, and there is money in it to return.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public bool CanRefundPayment => order is not null && order.Status == OrderStatus.Completed && order.PaidKopecks > 0;

    /// <summary>Whether «Дописать» is offered: the order is paid for and was not voided.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public bool CanAddContactDetails =>
        order is not null && order.Status != OrderStatus.Cancelled && order.IsFullyPaid;

    /// <summary>Whether the void control is offered on this page.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public bool CanCancel => order is not null && order.Status != OrderStatus.Cancelled;

    /// <summary>The void button's label. Names the return when there is money to return.</summary>
    public string CancelText => order is { PaidKopecks: > 0 }
        ? "Отменить и вернуть деньги"
        : "Отменить заказ";

    /// <summary>The void button's accessible description, carrying the amount that goes back.</summary>
    public string CancelHint => order is { PaidKopecks: > 0 }
        ? $"Отменить заказ и вернуть клиенту {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))}"
        : "Отменить заказ";

    /// <summary>The payment line: what was paid, what came back and what is left.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public string PaymentSummary => order is null
        ? string.Empty
        : order.Status switch
        {
            OrderStatus.Cancelled => refundedTotal > 0
                ? $"Отменён, возвращено {TextFormat.Money(refundedTotal)}"
                : "Отменён, оплата не поступала",
            OrderStatus.Completed => DescribeClosedOrder(),
            _ => DescribeOpenOrder()
        };

    /// <summary>The two closed states. A completed order reads by how much of it still stands.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    private string DescribeClosedOrder()
    {
        if (refundedTotal > 0)
        {
            return order!.PaidKopecks > 0
                ? $"Оплачено {TextFormat.Money(collectedTotal)}, возвращено {TextFormat.Money(refundedTotal)}, осталось {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))}"
                : $"Оплата возвращена полностью: {TextFormat.Money(refundedTotal)}";
        }

        return order!.IsFullyPaid
            ? "Оплачен полностью"
            : $"Оплачено {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))}";
    }

    /// <summary>The open states — the only ones where money can still arrive.</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>

    private string DescribeOpenOrder() => order!.PaymentState switch
    {
        PaymentState.Paid => "Оплачен полностью",
        PaymentState.PartiallyPaid => $"Оплачено {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))} · осталось {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}",
        _ => $"Не оплачен · к оплате {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}"
    };

    /// <summary>The line's colour. Grey for money that has gone back: it is neither a debt nor a success, and green beside a refund row would tell the operator the opposite of what happened.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public Color PaymentColor => order switch
    {
        null => PaletteAccess.Resolve("Gray600", "Gray400"),
        { Status: OrderStatus.Cancelled } => PaletteAccess.Resolve("Gray600", "Gray400"),
        { Status: OrderStatus.Completed } when refundedTotal > 0 && order.PaidKopecks > 0 => PaletteAccess.Resolve("WarningText", "WarningDark"),
        { Status: OrderStatus.Completed } when refundedTotal > 0 => PaletteAccess.Resolve("Gray600", "Gray400"),
        { PaymentState: PaymentState.Paid } => PaletteAccess.Resolve("Success", "SuccessDark"),
        { PaymentState: PaymentState.PartiallyPaid } => PaletteAccess.Resolve("WarningText", "WarningDark"),
        _ => PaletteAccess.Resolve("Danger", "DangerDark")
    };

    /// <summary>── Refunds ──────────────────────────────────────────────────────────────────────────────── Summed from the ledger rows, not read off PaidKopecks.</summary>
    /// <remarks>Почему так - `docs/decisions/order-details.md`</remarks>


    private decimal refundedTotal;
    private decimal collectedTotal;

    /// <summary>What has come back out of the till on this order.</summary>
    public decimal RefundedTotal => refundedTotal;

    /// <summary>Gross collected, refunds excluded. Net is <c>Order.PaidKopecks</c>.</summary>
    public decimal CollectedTotal => collectedTotal;

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand SaveCommand { get; }

    /// <summary>Opens the «Дописать» sheet: phone and promised time on an order already paid for.</summary>
    public IAsyncRelayCommand AddContactDetailsCommand { get; }
    public IAsyncRelayCommand AddProductCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> IncreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> DecreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> RemoveItemCommand { get; }

    /// <summary>Re-prices one line by hand; the allowed price stays and is shown struck through.</summary>
    public IAsyncRelayCommand<OrderEditItemViewModel> EditItemPriceCommand { get; }

    /// <summary>Opens the composition sheet for a bundle line — the same sheet the cart uses.</summary>
    public IAsyncRelayCommand<OrderEditItemViewModel> EditItemCompositionCommand { get; }

    /// <summary>Opens and closes the collapsed fulfilment block. See <see cref="IsFulfilmentExpanded"/>.</summary>
    public IRelayCommand ToggleFulfilmentCommand { get; }

    public IAsyncRelayCommand CollectPaymentCommand { get; }

    /// <summary>Returns money on a finished order, in whole rubles, as a partial refund.</summary>
    public IAsyncRelayCommand RefundPaymentCommand { get; }

    /// <summary>Voids the whole order, refunding in full and settling the stock disposition.</summary>
    public IAsyncRelayCommand CancelOrderCommand { get; }

    public IAsyncRelayCommand BackCommand { get; }

    /// <summary>Points the ViewModel at an order and loads it.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public void ShowOrder(Guid id)
    {
        orderId = id;
        _ = LoadAsync();
    }
}
