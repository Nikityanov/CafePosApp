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

/// <summary>
/// Menu and cart. The cart is autosaved as a draft (crash safe) and can be parked;
/// the order itself is created by <see cref="ICheckoutService"/> in one transaction.
/// </summary>
public partial class MenuViewModel : ObservableObject
{
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ICatalogService catalog;
    private readonly IComboService combos;
    private readonly ICheckoutService checkout;
    private readonly IDraftOrderService drafts;
    private readonly IInventoryService inventory;
    private readonly IModifierPicker modifierPicker;
    private readonly IVariantPicker variantPicker;
    private readonly IDraftPicker draftPicker;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly IPaymentSheet paymentSheet;
    private readonly IOrderTimePicker timePicker;
    private readonly IComboEditor comboEditor;
    private readonly TimeProvider timeProvider;
    private readonly INavigationService navigation;
    private readonly IShiftSession shiftSession;
    private readonly ILogger<MenuViewModel> logger;

    private readonly SemaphoreSlim loadGate = new(1, 1);
    // Serialises the cart autosave. Two autosaves must never write the active-cart row
    // concurrently: they run on separate DbContexts, and the loser got
    // DbUpdateConcurrencyException ("expected 1 row, affected 0"), which meant the draft was
    // silently not persisted at all and a killed app lost the customer's cart.
    private readonly SemaphoreSlim autoSaveGate = new(1, 1);
    private CancellationTokenSource? autoSaveCancellation;

    public MenuViewModel(
        ICatalogService catalog,
        IComboService combos,
        ICheckoutService checkout,
        IDraftOrderService drafts,
        IInventoryService inventory,
        IModifierPicker modifierPicker,
        IVariantPicker variantPicker,
        IDraftPicker draftPicker,
        IDialogService dialogs,
        IHapticService haptics,
        IPaymentSheet paymentSheet,
        IOrderTimePicker timePicker,
        IComboEditor comboEditor,
        INavigationService navigation,
        IShiftSession shiftSession,
        TimeProvider timeProvider,
        ILogger<MenuViewModel> logger)
    {
        this.catalog = catalog;
        this.combos = combos;
        this.checkout = checkout;
        this.drafts = drafts;
        this.inventory = inventory;
        this.modifierPicker = modifierPicker;
        this.variantPicker = variantPicker;
        this.draftPicker = draftPicker;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.paymentSheet = paymentSheet;
        this.timePicker = timePicker;
        this.comboEditor = comboEditor;
        this.navigation = navigation;
        this.shiftSession = shiftSession;
        this.timeProvider = timeProvider;
        this.logger = logger;

        Cart.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanCreateOrder));
            ScheduleAutoSave();
        };

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        AddProductCommand = new AsyncRelayCommand<Product>(AddProductAsync);
        AddComboCommand = new AsyncRelayCommand<MenuComboViewModel>(AddComboAsync);
        SelectCategoryCommand = new RelayCommand<CategoryMenuItemViewModel?>(SelectCategory);
        AddItemCommand = new RelayCommand<CartItemViewModel>(AddItem);
        RemoveItemCommand = new RelayCommand<CartItemViewModel>(RemoveItem);
        EditLinePriceCommand = new AsyncRelayCommand<CartItemViewModel>(EditLinePriceAsync);
        EditLineCompositionCommand = new AsyncRelayCommand<CartItemViewModel>(EditLineCompositionAsync);
        ToggleOrderTypeCommand = new RelayCommand(SwitchOrderType);
        EditPhoneCommand = new AsyncRelayCommand(EditPhoneAsync);
        ToggleFulfilmentCommand = new RelayCommand(() => IsFulfilmentExpanded = !IsFulfilmentExpanded);
        UndoRemoveCommand = new RelayCommand(UndoRemove);
        PickTimeCommand = new AsyncRelayCommand(PickTimeAsync);
        PayAndCreateCommand = new AsyncRelayCommand(PayAndCreateAsync);
        ParkOrderCommand = new AsyncRelayCommand(ParkOrderAsync);
        OpenParkedCommand = new AsyncRelayCommand(OpenParkedAsync);
    }

    public ObservableCollection<Product> Products { get; } = [];
    public ObservableCollection<Product> FilteredProducts { get; } = [];
    public ObservableCollection<CategoryMenuItemViewModel> Categories { get; } = [];
    public ObservableCollection<CartItemViewModel> Cart { get; } = [];

    /// <summary>
    /// The bundles on the menu board, as their own row rather than as more entries in
    /// <see cref="FilteredProducts"/>.
    /// </summary>
    /// <remarks>
    /// A separate row is the correct shape, not a fallback: <see cref="Combo"/> has no category, so
    /// folding bundles into the product grid would put them under whatever section chip happened to be
    /// selected — or hide every bundle the moment one was. They have no section, so they get no
    /// section filter, and a row of their own is where that reads correctly. It also leaves the
    /// product grid's measured column-width contract (see
    /// <see cref="MenuViewModel.ProductColumnSpan"/> and the GridItemsLayout comment in MenuPage.xaml)
    /// exactly as it was, instead of making a bundle tile fight a product tile for a 140dp column.
    /// </remarks>
    public ObservableCollection<MenuComboViewModel> Combos { get; } = [];

    // ── Product grid width contract ──────────────────────────────────────────────────────────────────
    // The two-column grid clipped on narrow windows, and the fix had to be found empirically
    // because .NET MAUI 10 offers no way to state a column width:
    //   * ItemsLayout.ItemWidth / ItemHeight were REMOVED from ItemsLayout in 10.0.101 (only
    //     Orientation, SnapPointsAlignment and SnapPointsType are left), so the grid cannot be told
    //     how wide a cell is;
    //   * WidthRequest AND MaxWidthRequest on the card are both ignored for sizing — the platform's
    //     ItemsWrapGrid sets the container width itself. A literal WidthRequest="150" still
    //     measured 190px columns; MaxWidthRequest="150" shrank the card's content without moving
    //     the column at all.
    // What the grid still honours is Span, so the contract is expressed as the span: two columns
    // while each one is still wide enough to be useful, one below that. A single full-width column
    // cannot be clipped, and a café tablet still gets the two-column board.
    //
    // This also removes the latch. ItemsWrapGrid keeps whatever column width it settled on, so a
    // window narrowed after startup kept the wide window's columns and the pair overran the content
    // box — measured: at a 340px window the first column stayed at 170px and the pair overran by
    // 7px. Changing the span re-lays the grid out, so the width is derived from the current page
    // width every time instead of from history.
    //
    // The four constants mirror Views/MenuPage.xaml and must be changed with it, and are in device
    // independent pixels — Window.Width is DIPs, which on this machine's 125% display is 0.8x the
    // physical window width (measured: a 460px window reports 368).
    private const double PageHorizontalPadding = 32;      // Grid Padding="16", both sides
    private const int ProductColumns = 2;                 // GridItemsLayout Span when there is room
    private const double ProductColumnSpacing = 4;        // GridItemsLayout HorizontalItemSpacing
    private const double MinimumProductCardWidth = 140;   // below this a two-up card is cramped

    /// <summary>Span before the first layout pass has reported a width.</summary>
    private const int DefaultProductColumnSpan = ProductColumns;

    private double availableWidth;

    /// <summary>
    /// The window's width in device independent pixels, pushed in from <c>Views.MenuPage</c>
    /// whenever it is resized. Feeds <see cref="ProductColumnSpan"/>; not bound from XAML.
    /// </summary>
    public double AvailableWidth
    {
        get => availableWidth;
        set
        {
            // Resizing produces a stream of fractional widths; a one-pixel threshold keeps the
            // property — and therefore the whole grid — from being re-laid out on every frame.
            if (Math.Abs(availableWidth - value) < 1) return;
            availableWidth = value;
            // Raised for itself as well as for the derived span: it is a public property, and a
            // binding to it would otherwise latch the first value it ever saw.
            OnPropertyChanged(nameof(AvailableWidth));
            OnPropertyChanged(nameof(ProductColumnSpan));
        }
    }

    /// <summary>
    /// Product cards per row: two while each column would still be at least
    /// <see cref="MinimumProductCardWidth"/> wide, one below that. Applied by the page to the
    /// named <c>GridItemsLayout</c>, which is the only width-related member the grid still has.
    /// </summary>
    public int ProductColumnSpan
    {
        get
        {
            if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
                return DefaultProductColumnSpan;

            var content = availableWidth
                          - PageHorizontalPadding
                          - ProductColumnSpacing * (ProductColumns - 1);

            return content / ProductColumns >= MinimumProductCardWidth ? ProductColumns : 1;
        }
    }

    private decimal total;

    /// <summary>
    /// The cart total. Setting it raises <see cref="TotalText"/> as well, because the checkout button
    /// binds the formatted amount inside its own caption.
    /// </summary>
    public decimal Total
    {
        get => total;
        private set
        {
            // …and the button caption, which is the ONE place the total is now printed. A total that
            // moved while the button still quoted the old one would be a button promising a price the
            // checkout does not charge.
            if (SetProperty(ref total, value))
            {
                OnPropertyChanged(nameof(TotalText));
                OnPropertyChanged(nameof(PayAndCreateText));
            }
        }
    }

    /// <summary>The cart total in the active currency, e.g. "740,00 ₿".</summary>
    public string TotalText => TextFormat.Money(Total);

    /// <summary>
    /// The checkout button's caption, which is the ACTION and the AMOUNT it will charge:
    /// «Оплатить 220.00 ₽».
    /// </summary>
    /// <remarks>
    /// <b>Why the figure is inside the button and not beside it.</b> The owner's decision, over
    /// placing the total in the row to the left of the button: both cost the same row, and this leaves
    /// one focus point instead of a figure and a verb the operator has to associate before pressing
    /// anything. A button that NAMES what it charges also answers the question Baymard's 2024 study
    /// recorded a tester asking verbatim — «I'm not sure if I click the 'Next' button, will it
    /// charge?» — which is a question about this exact control.
    /// <para>
    /// The «Итого» row this replaced is gone from the markup rather than hidden, and the separator
    /// above the footer stayed: what it divided (the cart lines from the footer) still exists.
    /// </para>
    /// </remarks>
    public string PayAndCreateText => $"Оплатить {TotalText}";

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set { if (SetProperty(ref isBusy, value)) OnPropertyChanged(nameof(CanCreateOrder)); } }

    public bool CanCreateOrder => !IsBusy && Cart.Count > 0;

    private string message = string.Empty;
    public string Message
    {
        get => message;
        private set
        {
            // Any plain message clears the error flag, so a failure cannot stay on screen
            // styled as a success after the next successful action. SetError assigns Message
            // first and the flag second, so ordering makes the pair work either way round.
            if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage));
            if (!string.IsNullOrWhiteSpace(value)) IsErrorMessage = false;

            // …and it retires the pending undo. One message owns the strip, so an «Отменить»
            // left sitting beside "Номер сохранён." would offer to put back a line whose
            // removal the operator has long since moved past. A removal therefore arms its
            // undo AFTER writing its own message — see RemoveItem.
            ClearPendingUndo();
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    // ── The restored draft, said in the «Корзина» header ───────────────────────────────────────
    // NOT part of Message, and the reason is cost: the message strip sits above the checkout
    // button, so a restored-draft note there costs a whole row of the operator's most valuable
    // space to say something that is not an outcome of anything they just did. In the header it
    // becomes a continuation of «Корзина» and costs nothing.
    //
    // It is also not transient in the way a message is. Message is retired by the next action
    // (its setter clears the error flag and the pending undo); this note describes a standing fact
    // about the cart — the lines below came from an unsaved order — and stays until the cart is
    // cleared or replaced. Fading it away would make the operator stop trusting it.
    private string draftNotice = string.Empty;
    public string DraftNotice
    {
        get => draftNotice;
        private set
        {
            if (SetProperty(ref draftNotice, value)) OnPropertyChanged(nameof(HasDraftNotice));
        }
    }
    public bool HasDraftNotice => !string.IsNullOrWhiteSpace(DraftNotice);

    // ── Undo of a removal ───────────────────────────────────────────────────────────────────────
    // Baymard's five requirements for a cart include "provide an undo option if a cart item is
    // removed", and NN/g found users "accidentally added the same item to their cart multiple
    // times" — both point the same way: removing is the mistake worth making recoverable, adding
    // is not, because a second add is obvious on the row itself.
    //
    // NO TIMER, and that is the design rather than an omission. The undo lives in the message strip
    // that was already there, so it costs no additional row: the strip is present whenever a
    // message is, and absent otherwise. It is retired by the NEXT message rather than by a clock
    // (Message's setter calls ClearPendingUndo), so «Отменить» is offered for exactly the outcome it
    // belongs to and can never sit beside an unrelated one. Nothing has to be torn down on a timer,
    // so nothing leaks if the page is left.
    //
    // The removed line is put back at the INDEX it was removed from, not appended: a cart read
    // top-to-bottom is a sequence, and a cashier who removes the third line and undoes it expects
    // the third line back.

    private PendingUndo? pendingUndo;

    /// <summary>Whether the message strip is currently offering to put a line back.</summary>
    public bool HasUndo => pendingUndo is not null;

    private void ClearPendingUndo()
    {
        if (pendingUndo is null) return;
        pendingUndo = null;
        OnPropertyChanged(nameof(HasUndo));
    }

    /// <summary>Puts the line the last message removed back where it was.</summary>
    private void UndoRemove()
    {
        if (pendingUndo is not { } pending) return;

        // Disarmed FIRST, because the confirmation below goes through Message and its setter
        // clears the pending undo anyway — arming nothing and leaving that to the message would
        // work, but only by accident of ordering.
        ClearPendingUndo();

        // THE QUANTITY HAS TO BE PUT BACK TOO, and this is not a detail. RemoveItem decrements the
        // line's own Quantity before removing it, so the instance held here arrives with 0 on it.
        // Re-inserting it as it stands puts a row on the cart reading "0" and "Итого 0,00", and the
        // checkout then fails in the domain with «У каждой позиции заказа должно быть положительное
        // количество» — which is exactly what the emulator showed the first time this ran. Restoring
        // means "put the line back AS IT WAS", not merely "put the object back".
        pending.Item.Quantity = pending.Quantity;
        // The index can be out of range if the cart changed underneath (a draft restored, a line
        // added and removed again); appending is the honest fallback rather than throwing from a
        // tap on an "Отменить" button.
        if (pending.Index >= 0 && pending.Index <= Cart.Count) Cart.Insert(pending.Index, pending.Item);
        else Cart.Add(pending.Item);

        Recalculate();
        Message = $"{pending.Item.ProductName} — возвращено в корзину.";
        haptics.Click();
    }

    /// <summary>A removed line, and where it was.</summary>
    private readonly record struct PendingUndo(CartItemViewModel Item, int Index, int Quantity);

    private bool isErrorMessage;
    /// <summary>
    /// Whether <see cref="Message"/> reports a failure. One string carried both outcomes
    /// and the label was styled SecondaryLabel either way, so the confirmation of a created
    /// order and a failed add looked alike — and the confirmation sat last on the screen,
    /// under the button, in the flow where it matters most. Set via <see cref="SetError"/>.
    /// </summary>
    public bool IsErrorMessage
    {
        get => isErrorMessage;
        private set
        {
            if (!SetProperty(ref isErrorMessage, value)) return;
            // The colour is derived from this flag, so it has to be announced with it.
            OnPropertyChanged(nameof(MessageColor));
        }
    }

    /// <summary>
    /// The ink for <see cref="Message"/>: red on a failure, green otherwise.
    /// </summary>
    /// <remarks>
    /// This label was MEASURED wrong on a Pixel 7 in dark theme: it painted
    /// <c>Success #2E7D32</c> on <c>#1E1E1E</c>, 3.28:1, because the colour arrived as a
    /// <c>Setter</c> inside a <c>DataTrigger</c> holding a bare <c>{StaticResource Success}</c> —
    /// and a trigger Setter takes a VALUE, not a binding expression, so the token is resolved
    /// once at parse time against the light theme and no later theme change can re-resolve it.
    /// Two triggers, one per outcome, so both branches were latched.
    /// <para>
    /// The fix is to stop using a trigger for a colour at all and resolve it here, against the
    /// live theme — <c>SuccessDark</c> measures 8.28:1 on <c>SurfaceDark</c>, which is the figure
    /// the measured 3.28:1 should have been.
    /// </para>
    /// <para>
    /// Both outcomes carry their own token, so unlike the untinted case elsewhere there is no
    /// "leave it to the implicit style" branch to reproduce. The text always says which one it is
    /// ("Заказ #1 создан…", "Добавьте товары в заказ."), so the colour reinforces the sentence and
    /// never carries the meaning alone.
    /// </para>
    /// </remarks>
    public Color MessageColor => IsErrorMessage
        ? PaletteAccess.Resolve("Danger", "DangerDark")
        : PaletteAccess.Resolve("Success", "SuccessDark");

    /// <summary>Sets a failure message and flags it as one.</summary>
    private void SetError(Exception exception, string prefix) =>
        (Message, IsErrorMessage) = (UserMessages.Describe(exception, prefix), true);

    private Category? selectedCategory;
    public Category? SelectedCategory { get => selectedCategory; private set => SetProperty(ref selectedCategory, value); }

    // ── Which chip is selected ──────────────────────────────────────────────────────────────────
    // A KEY, not a Category. The strip now holds three kinds of chip — «Все», the real categories
    // and «Комбо» — and two of them have no Category at all. The old test was
    //     chip.Category?.Id == SelectedCategory?.Id || (chip.IsAll && SelectedCategory is null)
    // which reads correctly for a strip of one kind of chip and is WRONG for this one: when no
    // category is selected both sides are null, so every category-less chip — «Все» AND «Комбо» —
    // compared equal and lit up together. Comparing the chip's own identity removes the whole class
    // of bug rather than adding a case to it.
    private Guid selectedFilterKey = MenuFilter.AllKey;
    public Guid SelectedFilterKey
    {
        get => selectedFilterKey;
        private set
        {
            if (!SetProperty(ref selectedFilterKey, value)) return;
            // IsCombosOnly and the product grid's visibility are both derived from the key, and
            // neither would be announced by a change to the key alone.
            OnPropertyChanged(nameof(IsCombosOnly));
        }
    }

    /// <summary>
    /// True while the «Комбо» chip is selected. The product grid is then hidden, because a bundle
    /// is not a <c>Product</c> and no amount of filtering <see cref="FilteredProducts"/> can produce
    /// one — «filter to combos only» means the grid is empty, not narrowed.
    /// </summary>
    public bool IsCombosOnly => SelectedFilterKey == MenuFilter.CombosKey;

    // ── Fulfilment, contact and time ────────────────────────────────────────────────────────────
    // Three facts about the ORDER rather than about any line: a customer either takes the whole
    // thing away or eats all of it on the premises, so they cannot be carried per cart line — which
    // is also why they are gathered here, above the cart, where the decision is made once.
    //
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // PROGRESSIVE DISCLOSURE — WHY THESE THREE CONTROLS ARE BEHIND ONE ROW
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // They used to be permanently expanded above the total, and they measured 153dp of the cart
    // block's 344dp on the emulator (see MenuPage.xaml for the [CartDiag] figures). The cart was
    // taller than the product grid it sits under.
    //
    // NN/g's hotel-reservation case is almost exactly this screen: a single-screen design "worked
    // well when users were trying to decide" but "caused usability problems because it included a
    // segment for users to enter their address and credit card information… it's not needed during
    // the exploratory phase". The exploratory phase here is building the cart, which is what the
    // operator spends the whole of this screen doing; the phone and the fulfilment mode are a
    // decision they make once per order, near the end. The rule the owner confirmed is FREQUENCY OF
    // USE, and that is the rule applied: the first level carries the two decisions that are always
    // made, as a statement of the current state, and the rest is one tap away.
    //
    // "Designs that go beyond 2 disclosure levels typically have low usability" — so this is the
    // limit and not the first of several layers. Nothing here is nested inside anything else; the
    // expanded block's children are all at the same level. And Chimera et al. 1994, over a 1296-item
    // hierarchy, found the "multipane and expand/contract interfaces produced significantly faster
    // times than the stable interface" — which is the direction this moves in, and the reason the
    // collapsed row is a real control rather than a label.
    //
    // WHY A ROW AND NOT A SHEET. MD3 restricts modal bottom sheets to mobile and suggests
    // floating/side sheets on tablets; Apple HIG says "Avoid using a sheet to help people navigate
    // your app's content"; and a sheet over a screen whose whole job is tapping dishes would swallow
    // a tap meant for the menu. So the block expands in place, under the row that opened it.
    //
    // WHY IT IS NOT RESET BY THE CART CHANGING. Nothing in the add/remove/quantity paths touches
    // IsFulfilmentExpanded, deliberately: the operator who opened the block to set a time, then
    // added a dish, must not have it collapse under them. Android does not recreate the activity on
    // rotation either — MainActivity declares ConfigurationChanges for ScreenSize | Orientation |
    // Density and is locked to Portrait — so the transient ViewModel injected into this page outlives
    // the whole session on the tab and the state needs no storage of its own.

    private bool isFulfilmentExpanded;

    /// <summary>
    /// Whether the fulfilment/contact/time block is showing its controls. Collapsed by default.
    /// </summary>
    /// <remarks>
    /// Plain state on the ViewModel, NOT on the page, and that placement is the point: a page-level
    /// flag would be lost the first time Shell rebuilt the page, and a ViewModel flag survives every
    /// round trip the operator can actually make. See the remarks above for the round trip this is
    /// measured against.
    /// </remarks>
    public bool IsFulfilmentExpanded
    {
        get => isFulfilmentExpanded;
        set
        {
            // FulfilmentSummary is raised with it, because the row's TEXT depends on whether the
            // block is open — see that property. It used to depend only on the order type and the
            // time, and the setter raised nothing for it, which is exactly how the two strings drift.
            if (SetProperty(ref isFulfilmentExpanded, value))
            {
                OnPropertyChanged(nameof(FulfilmentToggleHint));
                OnPropertyChanged(nameof(FulfilmentSummary));
            }
        }
    }

    /// <summary>
    /// The collapsed row's label, which is <b>two different strings</b> — and it is one computed
    /// property reading one piece of state, not two strings that have to be kept in step.
    /// </summary>
    /// <remarks>
    /// Collapsed, it STATES the current fulfilment («В зале · готово сейчас»). That is the whole job
    /// of the collapsed row: the controls that could change these facts are hidden, so nothing else on
    /// screen says them, and an operator must be able to see that the order is set up the way they
    /// meant without opening anything.
    /// <para>
    /// Expanded, it says only WHAT IS UNDER IT («Параметры выдачи») and nothing more. Expanded, the
    /// fulfilment control directly beneath it is «Сделать с собой» — a statement of the change, not
    /// of the state — and the row below that reads «Готово сейчас». Repeating «В зале · готово
    /// сейчас» above all three would print each fact twice within three rows, and the row would also
    /// contradict the button under it: a row reading «В зале» over a button reading «Сделать с
    /// собой» asks the reader to reconcile two statements of the same fact.
    /// </para>
    /// <para>
    /// Derived from <see cref="IsFulfilmentExpanded"/>, <see cref="OrderType"/> and
    /// <see cref="RequestedAt"/> in one place — through <see cref="RequestedTimeText"/>, which is the
    /// same string the «Когда» row under it prints. Two independent strings would let the collapsed row
    /// and the controls beneath it disagree, and nothing in the compiler would notice.
    /// </para>
    /// </remarks>
    public string FulfilmentSummary => IsFulfilmentExpanded
        ? FulfilmentRowTitle
        : $"{OrderTypeText} · готово {(requestedAt is null ? "сейчас" : $"к {RequestedTimeText}")}";

    /// <summary>
    /// The neutral heading the row falls back to while the block is open. A constant rather than an
    /// inline literal in the expression above, because <see cref="FulfilmentToggleHint"/> states it
    /// too and two copies of a caption are two things to reword.
    /// </summary>
    public const string FulfilmentRowTitle = "Параметры выдачи";

    /// <summary>
    /// What the collapsed row announces, and what pressing it does. A screen reader hears the two
    /// decisions AND the affordance — the chevron is a shape with no text of its own, so without this
    /// the row would be announced as a static line and nothing would say it opens.
    /// </summary>
    /// <remarks>
    /// Branched on the same <see cref="IsFulfilmentExpanded"/> the row's own caption branches on, so
    /// the announcement and the visible text cannot describe different states.
    /// </remarks>
    public string FulfilmentToggleHint => IsFulfilmentExpanded
        ? $"{FulfilmentRowTitle}. Свернуть параметры выдачи."
        : $"{FulfilmentSummary}. Показать параметры выдачи: способ выдачи, телефон и время.";

    /// <summary>
    /// How the order is fulfilled. <b>It is what decides whether a contact field exists at all</b> —
    /// see <see cref="IsTakeaway"/>.
    /// </summary>
    private OrderType orderType = OrderType.CounterService;
    public OrderType OrderType
    {
        get => orderType;
        private set
        {
            if (!SetProperty(ref orderType, value)) return;
            // Three dependents: the phone row's visibility and its empty state, and the checkout
            // intent's first field — announced together because they are one decision, which is also
            // why HasNoCustomerPhone is in the list: it is IsTakeaway AND no number, and a stale copy
            // of it would leave the row showing the wrong one of two labels.
            OnPropertyChanged(nameof(IsCounterService));
            OnPropertyChanged(nameof(IsTakeaway));
            OnPropertyChanged(nameof(HasNoCustomerPhone));
            // …the action button's own caption, which now names the change it makes rather than the
            // state it is in, and therefore has to be re-announced with it.
            OnPropertyChanged(nameof(ToggleOrderTypeText));
            // …and the collapsed row, which states this decision in words.
            OnPropertyChanged(nameof(FulfilmentSummary));
        }
    }

    /// <summary>True for counter service — the default, and the sale that stores the least personal data.</summary>
    public bool IsCounterService => OrderType == OrderType.CounterService;

    /// <summary>
    /// True when the customer takes the order away, and therefore the only case in which a phone is
    /// asked for.
    /// </summary>
    /// <remarks>
    /// 152-ФЗ ст. 6(1)(5) permits a phone to be processed only where it is needed for the performance
    /// of the contract, and ст. 5(7) requires erasing it once that purpose is met. A customer eating
    /// on the premises needs to be found in no way at all, so counter service does not collect one and
    /// <see cref="ICheckoutService"/> would drop it at the service boundary even if it arrived.
    /// EDPB Guidelines 2/2019 п. 25 is the same argument from the other side: where a less intrusive
    /// option exists, the processing is unnecessary in the first place.
    /// </remarks>
    public bool IsTakeaway => OrderType == OrderType.Takeaway;

    /// <summary>
    /// The two state words, kept apart from the button caption on purpose.
    /// </summary>
    /// <remarks>
    /// They were two public properties because the fulfilment control was two segments, each wearing
    /// the word for the state it set. It is one button now (see <see cref="ToggleOrderTypeText"/>), so
    /// the state is stated in exactly one place — the collapsed row, via
    /// <see cref="OrderTypeText"/> — and these are private. Same two strings, same spellings, so the
    /// collapsed row reads «В зале · готово сейчас» exactly as it read before.
    /// </remarks>
    private const string CounterServiceText = "В зале";

    private const string TakeawayText = "С собой";

    /// <summary>
    /// How the order is fulfilled, in the collapsed row's words. The same two strings and the same
    /// <c>OrderTypeText</c> shape <c>OrderDetailsViewModel</c> uses, so the two order screens cannot
    /// describe the same order differently.
    /// </summary>
    public string OrderTypeText => IsTakeaway ? TakeawayText : CounterServiceText;

    /// <summary>
    /// The one fulfilment button's caption: <b>the change it makes</b> — «Сделать с собой» on a
    /// counter-service order, «Сделать в зале» on a takeaway one.
    /// </summary>
    /// <remarks>
    /// Why not two segments. «В зале» / «С собой» restated what the collapsed row already said, and
    /// while the block was open the button would then have sat directly beneath a row saying «В
    /// зале» — the control restating the state its own caption denies. A button that names the
    /// change is unambiguous without a second segment to read, and it is one target instead of two,
    /// which is one fewer thing to mis-tap on a screen whose totals are money.
    /// <para>
    /// The current state is still reachable, at no cost in height: the collapsed row states it (and
    /// collapses back to «Параметры выдачи» only once this button is visible — see
    /// <see cref="FulfilmentSummary"/>), the button's own wording implies it by naming the opposite,
    /// and the phone row appears only for takeaway.
    /// </para>
    /// </remarks>
    public string ToggleOrderTypeText => IsTakeaway
        ? "Сделать в зале"
        : "Сделать с собой";

    /// <summary>
    /// What that button does, for a screen reader: the state, then the change — because the visible
    /// caption is deliberately the second and never the first.
    /// </summary>
    public string ToggleOrderTypeHint =>
        $"Сейчас: {OrderTypeText.ToLowerInvariant()}. {ToggleOrderTypeText}.";

    private string? customerPhone;

    /// <summary>
    /// The contact for a takeaway order, in E.164, or <c>null</c> — which is a normal state and not a
    /// missing value: the customer may decline to give one.
    /// </summary>
    /// <remarks>
    /// Held only while the order is on the cart and written by <see cref="ICheckoutService"/>, which
    /// normalises it and drops it entirely for counter service. A phone in this app is a contact for
    /// one order and nothing else — no SMS, no mailing, and typing a number subscribes the customer to
    /// nothing.
    /// </remarks>
    public string? CustomerPhone
    {
        get => customerPhone;
        private set
        {
            if (!SetProperty(ref customerPhone, value)) return;
            OnPropertyChanged(nameof(PhoneDisplayText));
            OnPropertyChanged(nameof(HasCustomerPhone));
            OnPropertyChanged(nameof(HasNoCustomerPhone));
        }
    }

    /// <summary>
    /// The number as the CUSTOMER may see it, masked: <c>+7 ••• ••• •• 42</c>.
    /// </summary>
    /// <remarks>
    /// <b>The cart is the customer-facing surface</b>, so the mask goes here and nowhere else on this
    /// page. PCI DSS 3.4.1 asks for at least six leading and four trailing digits of a PAN to be
    /// masked; it is written about a card number, so its figure is a floor rather than a shape, and
    /// <see cref="PhoneNumber.Mask"/> hides more than the floor. Eight of eleven digits stay covered on
    /// a Russian number, with enough left for the customer to recognise their own number as they read
    /// it back to the cashier — which is the whole point of the confirm step in
    /// <see cref="EditPhoneAsync"/>. The unmasked number belongs only on the staff-facing
    /// <c>OrderDetailsPage</c>.
    /// </remarks>
    public string PhoneDisplayText => customerPhone is null ? string.Empty : PhoneNumber.Mask(customerPhone);

    /// <summary>Whether a masked number is on screen to show.</summary>
    public bool HasCustomerPhone => !string.IsNullOrWhiteSpace(PhoneDisplayText);

    /// <summary>
    /// The empty state of the contact row for a takeaway order: asked, and not yet answered. A separate
    /// flag rather than a computed one so the row has a label in both states — a row that is simply
    /// blank reads as a control that has not loaded yet.
    /// </summary>
    public bool HasNoCustomerPhone => IsTakeaway && !HasCustomerPhone;

    // The two per-face style properties this ViewModel carried for the fulfilment toggle
    // (CounterServiceButtonStyle / TakeawayButtonStyle, off "PaymentMethodActive" /
    // "PaymentMethodButton") are GONE with the second segment. A one-button toggle has no second
    // face to tint, and a button that wears a "selected" style would be claiming a selection while
    // actually stating an action. The AppThemeBinding-inside-a-Setter trap they were written around
    // is still live elsewhere and still reasoned out at MessageColor.

    private DateTimeOffset? requestedAt;

    /// <summary>
    /// The time the customer asked for, or <c>null</c> for as soon as possible.
    /// </summary>
    /// <remarks>
    /// An absent time IS "as soon as possible" and the promise is then
    /// <c>CreatedAt + <see cref="Order.LeadTimeMinutes"/></c>. There is deliberately no ASAP flag
    /// beside this: a flag would store the same absence twice, and the two copies could disagree — one
    /// column saying "ASAP" while the other holds a time.
    /// </remarks>
    public DateTimeOffset? RequestedAt
    {
        get => requestedAt;
        private set
        {
            if (!SetProperty(ref requestedAt, value)) return;
            OnPropertyChanged(nameof(RequestedTimeText));
            OnPropertyChanged(nameof(IsRequestedTimeLate));
            OnPropertyChanged(nameof(PromiseText));
            // The collapsed row states the time in words too.
            OnPropertyChanged(nameof(FulfilmentSummary));
        }
    }

    /// <summary>
    /// Whether the named time is already behind us — an order promised for a moment that has gone,
    /// which the board shows as overdue.
    /// </summary>
    /// <remarks>
    /// MARKED, NOT REFUSED, and the sheet is where the decision is made. A customer who said «в 14:20»
    /// and is still waiting at 15:00 is the ordinary reason this control exists, and a sheet that
    /// refused the time would leave the cashier unable to write down what is actually true. The order
    /// books with a <c>PromisedAt</c> in the past; this is what says so at the till.
    /// <para>
    /// A computed reading of the wall clock rather than a stored flag, so it cannot disagree with the
    /// order — but it is therefore only as fresh as the last time something announced it. That is why
    /// <see cref="RequestedTimeText"/> and this are both re-raised by <see cref="LoadAsync"/>, which
    /// Shell drives on every return to the tab, and by every change to <see cref="RequestedAt"/>.
    /// </para>
    /// </remarks>
    public bool IsRequestedTimeLate => OrderPromise.IsLate(requestedAt, timeProvider.GetLocalNow());

    /// <summary>What the "когда" control currently reads: «сейчас», or the chosen clock time.</summary>
    public string RequestedTimeText => requestedAt is null
        ? "сейчас"
        : ClockTime.Format(requestedAt.Value, timeProvider.LocalTimeZone);

    /// <summary>
    /// The promise, as the CUSTOMER reads it. A range when the time is the till's to estimate, a clock
    /// time only when the customer named one.
    /// </summary>
    /// <remarks>
    /// <b>WHY A RANGE AND NOT A POINT.</b> Management Science 71(9) 2025, eight preregistered
    /// experiments, N=5323: a range is rated better than a point estimate even when the point estimate
    /// equals the range's upper bound — and an extremely wide range is rated WORSE than a point. So the
    /// figure below is widened, not shifted, and only modestly.
    /// <para>
    /// A requested time prints as a clock time on purpose, and it is not the same claim: it is what
    /// the customer asked for, repeated back so they can correct it — not an estimate the kitchen
    /// invented. Quoting a range for an agreed appointment would only obscure what was agreed.
    /// </para>
    /// </remarks>
    public string PromiseText => requestedAt is { } at
        ? $"Будет готово к {ClockTime.Format(at)}"
        : $"Будет готово через {PromiseFromMinutes}–{PromiseToMinutes} мин";

    /// <summary>
    /// How far the customer-facing range is widened around the lead time, in minutes.
    /// </summary>
    /// <remarks>
    /// Two minutes each way is the whole width of the claim: enough that the range is a range (4
    /// minutes of spread at the default 10-minute lead time, so it reads as a genuine estimate
    /// rather than a rounded single figure), and nowhere near the width at which the study's finding
    /// reverses. A range spanning half the lead time would be the "extremely wide" case that scores
    /// below a point estimate, and a zero-width range would be the point estimate the study shows
    /// losing.
    /// <para>
    /// The lead time itself is read from <see cref="Order.LeadTimeMinutes"/>, which
    /// <c>AddCafePosCore</c> assigns once at startup from <c>DatabaseOptions</c> — so the figure
    /// quoted to a customer is the same number the promise is computed from and there is no second
    /// constant here to drift.
    /// </para>
    /// </remarks>
    private const int PromiseSlackMinutes = 2;

    /// <summary>Lower bound of the quoted range. Floored at one minute, never zero.</summary>
    private static int PromiseFromMinutes => Math.Max(1, Order.LeadTimeMinutes - PromiseSlackMinutes);

    /// <summary>Upper bound: the lead time plus the slack, so the promise itself is inside the range.</summary>
    private static int PromiseToMinutes => Order.LeadTimeMinutes + PromiseSlackMinutes;

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<Product> AddProductCommand { get; }

    /// <summary>Adds a bundle to the cart through the one composition sheet.</summary>
    public IAsyncRelayCommand<MenuComboViewModel> AddComboCommand { get; }

    public IRelayCommand<CategoryMenuItemViewModel?> SelectCategoryCommand { get; }
    public IRelayCommand<CartItemViewModel> AddItemCommand { get; }
    public IRelayCommand<CartItemViewModel> RemoveItemCommand { get; }

    /// <summary>Re-prices one line by hand. The allowed price stays and is shown struck through.</summary>
    public IAsyncRelayCommand<CartItemViewModel> EditLinePriceCommand { get; }

    /// <summary>Opens the composition sheet for a bundle line, pre-filled and editable.</summary>
    public IAsyncRelayCommand<CartItemViewModel> EditLineCompositionCommand { get; }

    /// <summary>
    /// Flips the order between eating in and taking away, in one tap, from one button.
    /// </summary>
    /// <remarks>
    /// Replaces <c>SelectCounterServiceCommand</c> and <c>SelectTakeawayCommand</c>, which existed only
    /// to back the two segments. It calls the same <c>SelectOrderType</c> either way, so the legal and
    /// privacy consequences still run exactly as they did — a switch back to counter service still
    /// DROPS the phone rather than hiding the row (152-ФЗ ст. 6(1)(5)). One command also means there is
    /// no pair that can disagree about which state is live: the button reads
    /// <see cref="ToggleOrderTypeText"/>, which is derived from the same <see cref="OrderType"/>.
    /// <para>
    /// Neither direction writes to the message strip any more, because the state it used to announce is
    /// now on screen twice already — and a switch that DOES destroy a typed phone still does announce
    /// itself, since that loss is not readable off a row. See <c>SelectOrderType</c> for both halves.
    /// </para>
    /// </remarks>
    public IRelayCommand ToggleOrderTypeCommand { get; }

    /// <summary>Enters a phone, checks it, and has the cashier read it back to the customer.</summary>
    public IAsyncRelayCommand EditPhoneCommand { get; }

    /// <summary>
    /// Opens and closes the fulfilment/contact/time block in place.
    /// </summary>
    /// <remarks>
    /// A toggle rather than two commands, so the control that is on screen and the state it moves
    /// cannot disagree: there is no «Открыть» button that stays live after the block is already open.
    /// </remarks>
    public IRelayCommand ToggleFulfilmentCommand { get; }

    /// <summary>Puts back the line the last message removed. See the undo notes near <see cref="HasUndo"/>.</summary>
    public IRelayCommand UndoRemoveCommand { get; }

    /// <summary>Opens the "когда" sheet: as soon as possible, or a clock time.</summary>
    public IAsyncRelayCommand PickTimeCommand { get; }

    /// <summary>
    /// Takes the payment and books the order paid — or books it to be paid on collection, which the
    /// operator chooses inside the sheet. «Оплата при выдаче» left this screen and became a second
    /// exit from the payment sheet, so <c>CreateWithoutPaymentCommand</c> went with the button that
    /// used to carry it and this command now owns both outcomes.
    /// </summary>
    public IAsyncRelayCommand PayAndCreateCommand { get; }

    public IAsyncRelayCommand ParkOrderCommand { get; }
    public IAsyncRelayCommand OpenParkedCommand { get; }
}
