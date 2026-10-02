using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out of
// this change's write scope; it belongs beside ResourceStyles.TryGetColor. See its own remarks.
using CafePosApp.Converters;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

/// <summary>
/// One recorded payment, rendered as a single line on the details page.
/// </summary>
/// <remarks>
/// <see cref="IsRefund"/> is a separate field rather than something the wording implies,
/// because the two rows are the same shape in every other respect and a preformatted
/// single-string line made a refund line indistinguishable from a collection at the
/// template level. It is what <see cref="AmountColor"/> reads, and the template cannot
/// parse the text to find out the direction.
/// <para>
/// <see cref="AmountText"/> is the signed, formatted figure on its own and <see cref="Text"/>
/// is the composed sentence, split so the template can put the amount in its own column. The minus
/// sign is part of <see cref="AmountText"/>: the sign is the fastest thing to read on a row
/// list, and it must not depend on the surrounding prose to be noticed.
/// </para>
/// <para>
/// This was a positional record and became an ObservableObject, because <see cref="AmountText"/>
/// is now a re-derivable binding rather than a value frozen at construction: the operator can
/// change the currency while this line is on screen, and a string built once cannot follow. See
/// <see cref="RefreshMoneyText"/>.
/// </para>
/// </remarks>
public sealed class PaymentLine : ObservableObject
{
    private string text = string.Empty;
    private string amountText = string.Empty;

    /// <summary>The sentence: the method, the moment and any note. Never contains money.</summary>
    public string Text { get => text; set => SetProperty(ref text, value); }

    /// <summary>The amount in the active currency, signed for a refund. Bound by the row.</summary>
    public string AmountText { get => amountText; set => SetProperty(ref amountText, value); }

    public bool IsRefund { get; init; }

    /// <summary>The ink for <see cref="AmountText"/>.</summary>
    /// <remarks>
    /// This tint has been through two wrong answers, both worth recording. It was plain
    /// <c>{StaticResource Danger}</c> inside a <c>DataTrigger</c> Setter, which a trigger resolves
    /// once at parse time — a trigger's Setter takes a VALUE, not a binding expression — so the
    /// light token stayed in dark theme. It was then "fixed" by putting an
    /// <c>AppThemeBinding</c> in that same Setter, which is no better: an AppThemeBinding needs an
    /// <c>IProvideValueTarget</c> to register its theme-change callback against, and a trigger
    /// setter is not one, so it too collapses to a single parse-time value. Only a bound property
    /// resolves against the live theme. See MenuViewModel.MessageColor, which is the same story.
    /// <para>
    /// Numbers: this label is 12pt bold, which is UNDER the 14pt-bold large-text threshold, so it
    /// owes 4.5:1 as body text. The page has no CardBorder behind these rows, so the backdrop is
    /// the ContentPage fill — Danger <c>#D32F2F</c> on <c>BackgroundDark #121212</c> is 3.76:1 and
    /// fails; DangerDark <c>#FF9E8E</c> on the same surface is 9.40:1. Light theme is unchanged at
    /// Danger on <c>#FAFAFA</c>, 4.77:1, which passes.
    /// </para>
    /// <para>
    /// The untinted branch has to NAME a colour rather than defer to the implicit
    /// <c>Style TargetType="Label"</c>, because a local binding suppresses that style's own setter.
    /// <c>Resolve("Black", "White")</c> reproduces exactly what it declares
    /// (<c>AppThemeBinding Light=Black, Dark=White</c>, Resources/Styles/Styles.xaml). If that
    /// implicit style changes, this has to change with it.
    /// </para>
    /// <para>
    /// The tint is the THIRD signal and never the only one: the row text says «возврат» and the
    /// amount carries a minus sign, and <c>SemanticProperties</c> hands a screen reader the word,
    /// not the colour.
    /// </para>
    /// </remarks>
    public Color AmountColor => IsRefund
        ? ThemeColors.Resolve("Danger", "DangerDark")
        : ThemeColors.Resolve("Black", "White");

    /// <summary>Money leaving the till is the app's one negative-number case, so it is named once here.</summary>
    public static string SignedAmount(decimal amount, bool isRefund) =>
        isRefund ? $"−{TextFormat.Money(amount)}" : TextFormat.Money(amount);

    /// <summary>Re-raises <see cref="AmountText"/> after the currency setting changed.</summary>
    public void RefreshMoneyText() => OnPropertyChanged(nameof(AmountText));
}

public partial class OrderEditItemViewModel : ObservableObject
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    private int quantity;
    public int Quantity
    {
        get => quantity;
        set
        {
            if (!SetProperty(ref quantity, value)) return;
            OnPropertyChanged(nameof(LineTotal));
            // LineTotalText is what the row actually binds, so raising it here is what keeps the
            // printed amount in step with the stepper.
            OnPropertyChanged(nameof(LineTotalText));
        }
    }

    public decimal LineTotal => Price * Quantity;

    /// <summary>The line total in the active currency, e.g. "320,00 ₿". Bound by the row.</summary>
    public string LineTotalText => TextFormat.Money(LineTotal);

    /// <summary>Re-raises <see cref="LineTotalText"/> after the currency setting changed.</summary>
    public void RefreshMoneyText() => OnPropertyChanged(nameof(LineTotalText));

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
    private readonly IStockDispositionSheet stockDisposition;
    private readonly IDialogService dialogs;
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
        IStockDispositionSheet stockDisposition,
        IDialogService dialogs,
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
        this.stockDisposition = stockDisposition;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        AddProductCommand = new AsyncRelayCommand(AddProductAsync);
        IncreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(IncreaseItem);
        DecreaseItemCommand = new RelayCommand<OrderEditItemViewModel>(DecreaseItem);
        RemoveItemCommand = new RelayCommand<OrderEditItemViewModel>(RemoveItem);
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

    /// <summary>The order total in the active currency, e.g. "740,00 ₿".</summary>
    /// <remarks>
    /// The total is a sum over <see cref="Items"/>, so it changes whenever a line is added, removed
    /// or re-quantitied — none of which touch this ViewModel's own properties. Every mutation site
    /// therefore calls the private NotifyTotal(); the per-item quantities raise only their
    /// own LineTotalText, because the footer is this property's job.
    /// </remarks>
    public string TotalText => TextFormat.Money(Total);

    // ── Payment state ──────────────────────────────────────────────────────────────────────────
    // The order's own payment figures (PaidKopecks / BalanceKopecks / PaymentState) are the
    // source; these only render them. CanCollectPayment is false on a closed or cancelled order
    // even if the balance were non-zero, because the domain does not take payment on a closed one.

    /// <summary>True when the order is open and not fully paid — the only case a payment is taken.</summary>
    /// <remarks>
    /// Unchanged by the refund feature, and deliberately so. The status clause is what makes it
    /// safe: refunds only exist on <see cref="OrderStatus.Completed"/> orders, and a refunded order
    /// is still Completed, so a refunded order can never be re-charged through this path even though
    /// its <c>PaymentState</c> has fallen back to Unpaid or PartiallyPaid. Verified against the
    /// partially refunded case specifically — money held for it is money still in the till, and
    /// taking a second payment against it would credit the drawer twice for one sale.
    /// </remarks>
    public bool CanCollectPayment =>
        order is not null
        && order.PaymentState is not PaymentState.Paid
        && order.Status is OrderStatus.InProgress or OrderStatus.Ready;

    /// <summary>
    /// True when this order can have money returned: finished, and there is money in it to return.
    /// </summary>
    /// <remarks>
    /// The two conditions together, and neither is implied by the other. <c>Completed</c> because
    /// the domain restricts refunds to finished sales — the goods have left the bar, so giving the
    /// money back is the operator's decision, not a correction to a running total.
    /// <c>PaidKopecks &gt; 0</c> because it is the NET scalar: after a full refund it reads zero, so
    /// this is the check that stops the button appearing on an order that has nothing left to give
    /// back. Without it, an operator could open the sheet on a fully refunded order, key in an
    /// amount and be refused by the domain's «По заказу нечего возвращать» — the exact
    /// "control that can only fail" pattern the cancel button used to have.
    /// </remarks>
    public bool CanRefundPayment => order is not null && order.Status == OrderStatus.Completed && order.PaidKopecks > 0;

    /// <summary>
    /// Whether the void control is offered on this page.
    /// </summary>
    /// <remarks>
    /// Anything not already cancelled, for the same reason the board's <c>CanCancel</c> is: a paid
    /// order is voidable and cancelling takes the money with it. An already-cancelled order is
    /// excluded because the domain makes cancellation single-shot, and offering the control anyway
    /// would offer one that can only fail.
    /// </remarks>
    public bool CanCancel => order is not null && order.Status != OrderStatus.Cancelled;

    /// <summary>The void button's label. Names the return when there is money to return.</summary>
    public string CancelText => order is { PaidKopecks: > 0 }
        ? "Отменить и вернуть деньги"
        : "Отменить заказ";

    /// <summary>The void button's accessible description, carrying the amount that goes back.</summary>
    public string CancelHint => order is { PaidKopecks: > 0 }
        ? $"Отменить заказ и вернуть клиенту {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))}"
        : "Отменить заказ";

    /// <summary>
    /// The payment line: what was paid, what came back and what is left.
    /// </summary>
    /// <remarks>
    /// Branches on <c>Status</c> FIRST, then on the money — the ordering is the fix. This used to
    /// switch on <c>PaymentState</c> alone, which after a full refund renders
    /// «Не оплачен · к оплате 220.00 ₽» on a finished sale whose money has already gone back to the
    /// customer: correct arithmetic, completely wrong story, and it asks for money that cannot be
    /// taken because the order is closed. "Voided of its payment" is not "unpaid", and the only way
    /// to say that is to look at the status before the figures.
    /// <para>
    /// A cancelled order reads the same way — the money went back as part of the cancellation, so
    /// there is nothing owed and nothing to collect. The stock disposition is left out of this line
    /// because <c>StatusText</c> above already carries the composed cancellation reason, including it.
    /// </para>
    /// <para>
    /// Every branch names its amounts. Never colour alone: this line is the only place the state is
    /// stated in words, and a screen reader reads the sentence.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The two closed states. A completed order reads by how much of it still stands.
    /// </summary>
    /// <remarks>
    /// A partially refunded order deliberately does NOT fall into the "unpaid" wording even though
    /// its <c>PaymentState</c> is now <see cref="PaymentState.PartiallyPaid"/> — because it can still
    /// hold money (<c>PaidKopecks &gt; 0</c>) or hold none at all (fully refunded), and those are
    /// different sentences from anything the open-order branch below says, which promises to collect.
    /// </remarks>
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

    /// <summary>
    /// The open states — the only ones where money can still arrive. Wording unchanged from before
    /// the refund feature, on purpose: an order in progress that is short of its total is still an
    /// order that must be paid.
    /// </summary>
    private string DescribeOpenOrder() => order!.PaymentState switch
    {
        PaymentState.Paid => "Оплачен полностью",
        PaymentState.PartiallyPaid => $"Оплачено {TextFormat.Money(Money.FromKopecks(order.PaidKopecks))} · осталось {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}",
        _ => $"Не оплачен · к оплате {TextFormat.Money(Money.FromKopecks(order.BalanceKopecks))}"
    };

    /// <summary>
    /// The line's colour. Grey for money that has gone back: it is neither a debt nor a success, and
    /// green beside a refund row would tell the operator the opposite of what happened.
    /// </summary>
    /// <remarks>
    /// Was <c>Microsoft.Maui.Graphics.Colors.*</c> literals, which cannot follow the theme — see
    /// <c>ThemeColors</c>. Concretely: <c>Colors.Gray #808080</c> is 4.22:1 on a
    /// <c>SurfaceDark #1E1E1E</c> card and 3.49:1 on a <c>SurfaceVariantDark #2D2D2D</c> one, and
    /// this is a 13pt bold line carrying the whole payment story of a closed order. The palette's
    /// <c>Gray600</c>/<c>Gray400</c> — already this app's secondary-text pairing — gives 4.61:1
    /// in light and 8.87:1 in dark, with the same neutral reading. Paid, partial and unpaid move
    /// to <c>Success</c>/<c>Warning</c>/<c>Danger</c> and their dark counterparts.
    /// <para>
    /// The branch structure is untouched. It was already right, and it is why colour here is never
    /// the only cue: <see cref="PaymentSummary"/> states the same thing in words, including the
    /// figures, and it is what a screen reader reads.
    /// </para>
    /// </remarks>
    public Color PaymentColor => order switch
    {
        null => ThemeColors.Resolve("Gray600", "Gray400"),
        { Status: OrderStatus.Cancelled } => ThemeColors.Resolve("Gray600", "Gray400"),
        { Status: OrderStatus.Completed } when refundedTotal > 0 && order.PaidKopecks > 0 => ThemeColors.Resolve("WarningText", "WarningDark"),
        { Status: OrderStatus.Completed } when refundedTotal > 0 => ThemeColors.Resolve("Gray600", "Gray400"),
        { PaymentState: PaymentState.Paid } => ThemeColors.Resolve("Success", "SuccessDark"),
        { PaymentState: PaymentState.PartiallyPaid } => ThemeColors.Resolve("WarningText", "WarningDark"),
        _ => ThemeColors.Resolve("Danger", "DangerDark")
    };

    // ── Refunds ────────────────────────────────────────────────────────────────────────────────
    // Summed from the ledger rows, not read off PaidKopecks. PaidKopecks is NET (collected minus
    // refunded), so the gross collected figure the summary needs is not recoverable from it: an
    // order refunded all the way down to zero reads identically to one that was never paid.

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
    public IAsyncRelayCommand AddProductCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> IncreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> DecreaseItemCommand { get; }
    public IRelayCommand<OrderEditItemViewModel> RemoveItemCommand { get; }
    public IAsyncRelayCommand CollectPaymentCommand { get; }

    /// <summary>Returns money on a finished order, in whole rubles, as a partial refund.</summary>
    public IAsyncRelayCommand RefundPaymentCommand { get; }

    /// <summary>Voids the whole order, refunding in full and settling the stock disposition.</summary>
    public IAsyncRelayCommand CancelOrderCommand { get; }

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
