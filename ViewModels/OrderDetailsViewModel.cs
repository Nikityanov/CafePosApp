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

    /// <summary>
    /// The price this line was allowed to be sold at, read off the order's
    /// <see cref="OrderItem.ListPriceKopecks"/> and never rewritten here.
    /// </summary>
    /// <remarks>
    /// Shown struck through beside a changed price so the cashier sees the override while making it,
    /// exactly as on the cart. The allowed price is written once, when the line is created, and
    /// <c>UpdateOrderAsync</c> deliberately does not recompute it on an edit — recomputing it would
    /// write the new charged price straight back into the allowed price and leave the shift report
    /// nothing to compare.
    /// </remarks>
    public decimal ListPrice { get; init; }

    private decimal price;

    /// <summary>What the line is charged per unit. Editable by hand; see <see cref="ListPrice"/>.</summary>
    public decimal Price
    {
        get => price;
        set
        {
            if (!SetProperty(ref price, value)) return;
            OnPropertyChanged(nameof(PriceText));
            OnPropertyChanged(nameof(IsPriceOverridden));
        }
    }

    /// <summary>The allowed price formatted, printed struck through next to <see cref="PriceText"/>.</summary>
    public string ListPriceText => TextFormat.Money(ListPrice);

    /// <summary>The charged unit price in the active currency. Tapping it re-prices the line.</summary>
    public string PriceText => TextFormat.Money(Price);

    /// <summary>Whether the charged price differs from the allowed one — the override, visible at once.</summary>
    public bool IsPriceOverridden => Price != ListPrice;

    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    /// <summary>
    /// The bundle's composition, empty for an ordinary dish. Printed indented under the line, and part
    /// of the merge key so a save cannot rewrite one build of a bundle as another.
    /// </summary>
    public ObservableCollection<LineComponentViewModel> Components { get; } = [];

    /// <summary>True for a bundle: a line whose composition can be opened and changed.</summary>
    public bool IsCombo => Components.Count > 0;

    /// <summary>
    /// Whether this line and another are the same sale.
    /// </summary>
    /// <remarks>
    /// <see cref="OrderLineKey"/> and not an inline comparison, which is what this row's lookup used
    /// to do — and the inline version left the VARIANT out of the key entirely, so a large and a small
    /// of the same dish merged into one line. The composition is in the key too, so two different
    /// builds of one bundle stay two lines.
    /// </remarks>
    public string MergeKey => OrderLineKey.For(
        ProductId,
        SelectedModifierName,
        SelectedVariantName,
        Components.Select(component => (component.ProductId, component.QuantityPerUnit)));

    private int quantity;
    public int Quantity
    {
        get => quantity;
        set
        {
            if (!SetProperty(ref quantity, value)) return;
            OnPropertyChanged(nameof(LineTotal));
            // LineTotalText is NOT bound by the row any more: the row shows the unit price, which is what
            // re-pricing edits, and a total beside it was the same figure twice at quantity 1. It is
            // still raised because the decimal LineTotal is what the page's own Total sums.
            OnPropertyChanged(nameof(LineTotalText));
        }
    }

    public decimal LineTotal => Price * Quantity;

    /// <summary>The line total in the active currency, e.g. "320,00 ₿". Bound by the row.</summary>
    public string LineTotalText => TextFormat.Money(LineTotal);

    /// <summary>Re-raises the formatted amounts after the currency setting changed.</summary>
    public void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ListPriceText));
    }

    /// <summary>Re-announces <see cref="IsCombo"/> after the composition changed.</summary>
    public void OnCompositionChanged() => OnPropertyChanged(nameof(IsCombo));

    /// <summary>
    /// The entity this row saves as.
    /// </summary>
    /// <remarks>
    /// The composition is carried through, and that is load-bearing rather than cosmetic.
    /// <c>UpdateOrderAsync</c> matches incoming lines to the stored ones by <see cref="OrderLineKey"/>
    /// — which is built from the composition — and then rewrites a matched line's snapshot wholesale.
    /// A row that dropped its slots would therefore fail to match its own line, be inserted as a NEW
    /// line with no composition, and leave the original bundle composition behind on a line that no
    /// longer exists: a save of an untouched order would silently un-compose its bundles.
    /// </remarks>
    public OrderItem ToOrderItem() => new()
    {
        Id = Guid.NewGuid(),
        ProductId = ProductId,
        ProductName = ProductName,
        Price = Price,
        ListPriceKopecks = Money.ToKopecks(ListPrice),
        Quantity = Quantity,
        SelectedModifierName = SelectedModifierName,
        SelectedVariantName = SelectedVariantName,
        Components = Components.Select((component, index) => new OrderItemComponent
        {
            Id = Guid.NewGuid(),
            ProductId = component.ProductId,
            ProductName = component.Name,
            QuantityPerUnit = component.QuantityPerUnit,
            UnitPriceKopecks = component.UnitKopecks,
            ReferencePriceKopecks = component.ReferenceKopecks,
            SortOrder = index
        }).ToList()
    };
}

public partial class OrderDetailsViewModel : ObservableObject, IQueryAttributable
{
    private readonly IOrderOperations orders;
    private readonly ICatalogService catalog;
    private readonly IComboService combos;
    private readonly IComboEditor comboEditor;
    private readonly IModifierPicker modifierPicker;
    private readonly AppSettings settings;
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
        AppSettings settings,
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
        // CanExecute = CanEdit on the two COMMANDS the gesture recognizers use. It is what the Buttons
        // bind IsEnabled to, and it is also what stands in for the IsEnabled that used to sit on the
        // price's TapGestureRecognizer and crashed this template at inflation:
        // TapGestureRecognizer derives from GestureRecognizer : Element, so it is NOT a VisualElement
        // and has no IsEnabled at all. The assembly was read rather than recalled — its only public
        // members are Command, CommandParameter, NumberOfTapsRequired and Buttons — and SendTapped's
        // IL calls Command.CanExecute before Command.Execute, so a CanExecute of false is a genuinely
        // inert tap target rather than a tap that silently does nothing. NotifyOrderState raises
        // CanExecuteChanged whenever CanEdit moves, which is the half that is easy to forget.
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

    // ── Fulfilment, contact and promise ──────────────────────────────────────────────────────────
    // READ-ONLY HERE, AND THAT IS A KNOWN LIMITATION RATHER THAN A CHOICE. The till collects these
    // three on the cart (MenuViewModel) and CheckoutService writes them onto the order; the order editor
    // has no write path back, because IOrderService.UpdateOrderAsync takes only the item list. Until
    // Core grows an overload taking an OrderDetailsIntent, offering a control here would be a control
    // that looks editable and is not — so the page states the facts and edits nothing.
    //
    // What is shown is the EXACT promised time and the FULL number, both of which are staff-facing
    // facts on this screen and are the two things the customer-facing cart deliberately does not show.

    /// <summary>Whether the fulfilment/contact/promise card is worth showing at all.</summary>
    public bool HasOrderDetails => order is not null;

    // ── The same disclosure the cart uses, for the same reason ───────────────────────────────────
    // MenuPage collapsed its fulfilment block after it measured 153dp of a 344dp cart, and leaving
    // THIS card permanently expanded would be one screen reading two ways: dense on the till, loose
    // on the order. The grounding — NN/g's hotel reservation, the two-level ceiling, Chimera et al.
    // 1994 — is written out at length on MenuViewModel.IsFulfilmentExpanded and is not repeated.
    //
    // THE TRADE THIS SCREEN ADDS, STATED PLAINLY. The phone here is the FULL number and this is the
    // screen a cashier dials from, so collapsing puts one tap between the operator and the number.
    // That is the cost and it is accepted rather than discovered: what the collapse saves is two
    // short lines, and the collapsed row still states the fulfilment and the time, which is what the
    // operator scans. Nothing is removed, only moved one tap down.

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

    /// <summary>The collapsed row's label, and it is <b>two different strings</b>.</summary>
    /// <remarks>
    /// Collapsed, it states the order as it stands («В зале · готово к 18:00»). Expanded, it says
    /// only «Параметры заказа» and nothing more — the block directly beneath it reads «Выдача: В зале»
    /// and «Готово …», and repeating them in the row would print each fact twice inside two rows.
    /// <para>
    /// Same rule and same one-property construction as <see cref="MenuViewModel.FulfilmentSummary"/>,
    /// because the two order-entry screens are deliberately one design: both read their state from
    /// <see cref="IsFulfilmentExpanded"/> here, never from a second string kept in step by hand.
    /// </para>
    /// </remarks>
    public string FulfilmentSummary => order is null
        ? string.Empty
        : IsFulfilmentExpanded
            ? FulfilmentRowTitle
            : $"{OrderTypeText} · готово к {WhenText}";

    /// <summary>
    /// The neutral heading the row falls back to while the block is open. A constant because
    /// <see cref="FulfilmentToggleHint"/> states it too and two copies of a caption are two things to
    /// reword. Named for the page's own content, which is the order as it was placed — not the cart's
    /// «Параметры выдачи».
    /// </summary>
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

    /// <summary>
    /// The contact in FULL, or an empty string.
    /// </summary>
    /// <remarks>
    /// The one unmasked number in the app. This page is opened deliberately, for one order at a time,
    /// and it is the screen a cashier has to dial from — a masked number here would make the stored
    /// value unverifiable and the customer unreachable, which is the opposite of what 152-ФЗ
    /// ст. 6(1)(5) asks for. Everywhere else — the cart and the orders board — the number is masked,
    /// because those two are readable by whoever is standing in the room.
    /// <para>
    /// Empty for a counter order, and not merely hidden: <see cref="CheckoutService"/> drops the value
    /// at the service boundary, so there is nothing stored to show and this is an honest blank rather
    /// than a masked nothing.
    /// </para>
    /// </remarks>
    public string CustomerPhoneText => order?.CustomerPhone ?? string.Empty;

    /// <summary>Whether the order carries a contact worth printing on the block.</summary>
    public bool HasCustomerPhone => !string.IsNullOrWhiteSpace(CustomerPhoneText);

    /// <summary>Whether a phone is expected at all — takeaway only.</summary>
    public bool ExpectsPhone => order?.OrderType == OrderType.Takeaway;

    /// <summary>
    /// A counter-service order, where no phone was asked for and none is stored.
    /// </summary>
    /// <remarks>
    /// Said in words rather than left as a blank. «Телефон не спрашивали» and «телефон забыли» are
    /// different facts about the same empty row, and for an order eaten on the premises only the first
    /// one is correct — 152-ФЗ ст. 6(1)(5) permits the field only where the number is needed to perform
    /// the contract, so its absence here is the rule working, not a gap in the data.
    /// </remarks>
    public bool ExpectsNoPhone => order is not null && !ExpectsPhone;

    /// <summary>
    /// The promise, worded for what kind of promise it is.
    /// </summary>
    /// <remarks>
    /// A named time reads as «к 18:00» and the lead-time one as «обещано к 18:00». The customer saw a
    /// RANGE on the cart because that figure is an estimate the till chooses; here the exact time is
    /// what the order was promised and is being judged against, and a staff screen showing an estimate
    /// where a fact exists would be the wrong way round.
    /// </remarks>
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
    public Color OverdueColor => ThemeColors.Resolve("Danger", "DangerDark");

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
    /// Whether «Дописать» is offered: the order is paid for and was not voided.
    /// </summary>
    /// <remarks>
    /// Paid is the condition, and it is the owner's whole reason for the button — the customer thinks of a
    /// number AFTER the money has changed hands, so an order that is still unpaid is handled by the cart
    /// sheet it already came from, and offering the same facts twice in two places would let them disagree.
    /// <para>
    /// A voided order is excluded because its money went back and its total is gone: a customer who
    /// "remembers a number" for it is describing a different sale, and Core refuses it too. The two checks
    /// exist in both places deliberately — this one so the button is not offered for nothing, that one so
    /// a caller that skips the ViewModel still cannot write.
    /// </para>
    /// </remarks>
    public bool CanAddContactDetails =>
        order is not null && order.Status != OrderStatus.Cancelled && order.IsFullyPaid;

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

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("OrderId", out var value) && value is Guid id)
        {
            orderId = id;
            _ = LoadAsync();
        }
    }
}
