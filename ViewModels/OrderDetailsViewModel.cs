using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

/// <summary>
/// One recorded payment, rendered as a single line on the details page.
/// </summary>
public sealed record PaymentLine(string Text);

public partial class OrderEditItemViewModel : ObservableObject
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    private int quantity;
    public int Quantity { get => quantity; set { if (SetProperty(ref quantity, value)) OnPropertyChanged(nameof(LineTotal)); } }

    public decimal LineTotal => Price * Quantity;

    public OrderItem ToOrderItem() => new()
    {
        Id = Guid.NewGuid(),
        ProductId = ProductId,
        ProductName = ProductName,
        Price = Price,
        Quantity = Quantity,
        SelectedModifierName = SelectedModifierName,
        SelectedVariantName = SelectedVariantName
    };
}

public partial class OrderDetailsViewModel : ObservableObject, IQueryAttributable
{
    private readonly IOrderService orders;
    private readonly ICatalogService catalog;
    private readonly IModifierPicker modifierPicker;
    private readonly AppSettings settings;
    private readonly INavigationService navigation;
    private readonly IPaymentSheet paymentSheet;
    private readonly IHapticService haptics;
    private readonly ILogger<OrderDetailsViewModel> logger;

    private Guid orderId;
    private Order? order;

    public OrderDetailsViewModel(
        IOrderService orders,
        ICatalogService catalog,
        IModifierPicker modifierPicker,
        AppSettings settings,
        INavigationService navigation,
        IPaymentSheet paymentSheet,
        IHapticService haptics,
        ILogger<OrderDetailsViewModel> logger
    )
    {
        this.orders = orders;
        this.catalog = catalog;
        this.modifierPicker = modifierPicker;
        this.settings = settings;
        this.navigation = navigation;
        this.paymentSheet = paymentSheet;
        this.haptics = haptics;
        this.logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddProductCommand = new AsyncRelayCommand(AddProductAsync);
        IncreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(IncreaseItem);
        DecreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(DecreaseItem);
        RemoveItemCommand = new RelayCommand<OrderEditItemViewModel>(RemoveItem);
        CollectPaymentCommand = new AsyncRelayCommand(CollectPaymentAsync);
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

    // ── Payment state ──────────────────────────────────────────────────────────────────────────
    // The order's own payment figures (PaidKopecks / BalanceKopecks / PaymentState) are the
    // source; these only render them. CanCollectPayment is false on a closed or cancelled order
    // even if the balance were non-zero, because the domain does not take payment on a closed one.

    /// <summary>True when the order is open and not fully paid — the only case a payment is taken.</summary>
    public bool CanCollectPayment =>
        order is not null
        && order.PaymentState is not PaymentState.Paid
        && order.Status is OrderStatus.InProgress or OrderStatus.Ready;

    /// <summary>
    /// The payment line: what was paid and what is left. A partial payment reads differently from
    /// an unpaid one, and both say the amount so the state is never carried by colour alone.
    /// </summary>
    public string PaymentSummary => order?.PaymentState switch
    {
        PaymentState.Paid => "Оплачен полностью",
        PaymentState.PartiallyPaid => $"Оплачено {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))} · осталось {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}",
        _ when order is null => string.Empty,
        _ => $"Не оплачен · к оплате {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}"
    };

    public Color PaymentColor => order?.PaymentState switch
    {
        PaymentState.Paid => Colors.Green,
        PaymentState.PartiallyPaid => Colors.Orange,
        _ => Colors.Red
    };

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand AddProductCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> IncreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> DecreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> RemoveItemCommand { get; }
    public IAsyncRelayCommand CollectPaymentCommand { get; }
    public IAsyncRelayCommand BackCommand { get; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("OrderId", out var value) && value is Guid id)
        {
            orderId = id;
            _ = LoadAsync();
        }
    }
}
