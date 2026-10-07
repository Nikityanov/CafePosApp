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

        // The three Collaborators are built HERE, before anything reads them, and that position is
        // load-bearing twice over. The Cart subscription below reads a proxy onto cart, and the command
        // lambdas further down close over all three. The compiler caught the second case as CS8602 -
        // which is the one warning worth having here, because it is not a style complaint but a
        // genuine "this could be null at run time".
        //
        // They cannot be field initialisers either, because they read the constructor's parameters -
        // a field initialiser cannot see those. See MenuCatalogue for the clock.
        menu = new MenuCatalogue(catalog, combos, timeProvider);
        fulfilment = new FulfilmentEditor(timeProvider);
        cart = new CartBuilder();

        Cart.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanCreateOrder));
            ScheduleAutoSave();
        };

        // The editor owns the state; the shell owns the bindings. Re-announcing on every change is
        // what keeps a bound row reading the editor's value rather than a stale copy, and it is the
        // reason every proxy below is a plain read instead of a mirrored field.
        fulfilment.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        menu.PropertyChanged += (_, e) =>
        {
            // The chips' own selection state is announced by MenuCatalogue; what the shell owns is the
            // two properties XAML derives from it.
            if (e.PropertyName != nameof(MenuCatalogue.SelectedFilterKey)) return;
            OnPropertyChanged(nameof(SelectedCategory));
            OnPropertyChanged(nameof(IsCombosOnly));
            OnPropertyChanged(nameof(FilteredProducts));
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
        ToggleFulfilmentCommand = new RelayCommand(() => fulfilment.SetExpanded(!IsFulfilmentExpanded));
        UndoRemoveCommand = new RelayCommand(UndoRemove);
        PickTimeCommand = new AsyncRelayCommand(PickTimeAsync);
        PayAndCreateCommand = new AsyncRelayCommand(PayAndCreateAsync);
        ParkOrderCommand = new AsyncRelayCommand(ParkOrderAsync);
        OpenParkedCommand = new AsyncRelayCommand(OpenParkedAsync);


        // The cart owns the total now, but the button that QUOTES it is the shell's, so the shell has to
        // hear about the total moving. Without this the checkout button goes on quoting the amount the
        // cart had before the change — a button promising a price the checkout does not charge, and
        // exactly the defect the old Total setter existed to prevent.
        cart.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(CartBuilder.Total):
                case nameof(CartBuilder.HasUndo):
                    OnPropertyChanged(e.PropertyName);
                    OnPropertyChanged(nameof(PayAndCreateText));
                    OnPropertyChanged(nameof(CanCreateOrder));
                    break;
                case nameof(CartBuilder.TotalText):
                    OnPropertyChanged(nameof(TotalText));
                    OnPropertyChanged(nameof(PayAndCreateText));
                    break;
            }
        };
    }

    /// <summary>
    /// The menu board: dishes, bundle tiles and the category strip, with the filter over all three.
    /// The second Collaborator, and the one with the smallest dependency list the plan predicted.
    /// </summary>
    /// <remarks>
    /// The shell keeps <see cref="Products"/>, <see cref="FilteredProducts"/>, <see cref="Categories"/>
    /// and <see cref="Combos"/> as one-line proxies because XAML binds them here by name, and the
    /// catalogue owns them. <see cref="SelectedCategory"/> and <see cref="IsCombosOnly"/> are computed
    /// from the catalogue's key, which is why the shell has no filter state of its own.
    /// </remarks>
    private readonly MenuCatalogue menu;

    /// <summary>
    /// The cart, its total and its undo. One of the six Collaborators, and the only one with no
    /// dependencies of its own.
    /// </summary>
    /// <remarks>
    /// The shell keeps <see cref="Cart"/>, <see cref="Total"/> and <see cref="TotalText"/> as one-line
    /// proxies because XAML binds them here by name, and keeps <see cref="PayAndCreateText"/> and
    /// <see cref="CanCreateOrder"/> outright because they quote the total inside a control that also
    /// depends on <see cref="IsBusy"/>, which is the shell's. The subscription in the constructor is
    /// what keeps those two honest: without it the checkout button would go on quoting the amount the
    /// cart had before the change.
    /// </remarks>
    private readonly CartBuilder cart;

    /// <summary>The cart's lines, in the order the operator read them.</summary>
    public ObservableCollection<CartItemViewModel> Cart => cart.Cart;

    public decimal Total => cart.Total;

    /// <summary>Every dish the till knows, before filtering.</summary>
    public ObservableCollection<Product> Products => menu.Products;

    /// <summary>The dishes on the grid: the selected category's, inside their time window.</summary>
    public ObservableCollection<Product> FilteredProducts => menu.FilteredProducts;

    /// <summary>The category strip, «Все» first and «Комбо» last.</summary>
    public ObservableCollection<CategoryMenuItemViewModel> Categories => menu.Categories;

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
    public ObservableCollection<MenuComboViewModel> Combos => menu.Combos;

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

    /// <summary>The cart total in the active currency, e.g. «740,00 ₿».</summary>
    public string TotalText => cart.TotalText;

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

    /// <summary>Whether the message strip is currently offering to put a line back.</summary>
    /// <remarks>
    /// A proxy, like <see cref="Cart"/> and <see cref="Total"/>. The state itself is
    /// <see cref="CartBuilder"/>'s; the shell re-announces it because the strip that shows it is
    /// bound here.
    /// </remarks>
    public bool HasUndo => cart.HasUndo;

    /// <summary>Retires a pending undo, as <c>Message</c>'s setter does for every message.</summary>
    private void ClearPendingUndo() => cart.ClearPendingUndo();

    /// <summary>Puts the line the last message removed back where it was, and says so.</summary>
    /// <remarks>
    /// The restore itself is <see cref="CartBuilder.Restore"/>'s, down to the quantity and the index —
    /// the two things that cost an emulator session when they were wrong. What stays here is the two
    /// things the cart must not know about: the sentence, and the haptic.
    /// </remarks>
    private void UndoRemove()
    {
        var restored = cart.Restore();
        if (restored is null) return;

        Message = $"{restored}— возвращено в корзину.";
        haptics.Click();
    }


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

    /// <summary>The chosen category, or <c>null</c> for everything. The catalogue's, computed.</summary>
    public Category? SelectedCategory => menu.SelectedCategory;

    // ── Which chip is selected ──────────────────────────────────────────────────────────────────
    // A KEY, not a Category, and it belongs to the catalogue now. The strip holds three kinds of chip —
    // «Все», the real categories and «Комбо» — and two of them have no Category at all. The old test
    // was chip.Category?.Id == SelectedCategory?.Id || (chip.IsAll && SelectedCategory is null), which
    // reads correctly for a strip of one kind of chip and is WRONG for this one: when no category is
    // selected both sides are null, so every category-less chip — «Все» AND «Комбо» — compared equal
    // and lit up together. Comparing the chip's own identity removes the whole class of bug rather
    // than adding a case to it. See MenuFilter.
    public Guid SelectedFilterKey => menu.SelectedFilterKey;

    /// <summary>
    /// True while the «Комбо» chip is selected. The product grid is then hidden, because a bundle
    /// is not a <c>Product</c> and no amount of filtering <see cref="FilteredProducts"/> can produce
    /// one — «filter to combos only» means the grid is empty, not narrowed.
    /// </summary>
    public bool IsCombosOnly => menu.IsBundlesOnly;
    // ─ Fulfilment, contact and time ────────────────────────────────────────────────────────────────────────────────────
    // Three facts about the ORDER rather than about any line, all of them now FulfilmentEditor's.
    //
    // EVERY MEMBER IN THIS SECTION IS GET-ONLY, AND THAT IS THE POINT. The first version of these
    // proxies kept their own backing fields and setters, so a write landed in the shell's field
    // while the getter read the editor's - two states, and the write invisible to every read.
    // The_cart_starts_as_counter_service_and_the_toggle_moves_it caught it. Removing the write
    // surface altogether makes that class of bug unrepresentable rather than merely fixed.
    // Writes go through the editor's named operations - Select, SetPhone, SetPromise,
    // SetExpanded, Reset. See the editor for why those are named rather than properties.

    /// <summary>The three facts about the ORDER: fulfilment, phone, promised time.</summary>
    /// <remarks>
    /// The third Collaborator. Every member below is a proxy onto
    /// <see cref="FulfilmentEditor"/> because MenuPage.xaml binds them here by name; the state is
    /// the editor's and the values are not mirrored. The constructor subscribes and re-announces, so
    /// a binding reads the editor's value rather than a stale copy.
    /// </remarks>
    private readonly FulfilmentEditor fulfilment;

    /// <summary>Whether the fulfilment block is open.</summary>
    public bool IsFulfilmentExpanded => fulfilment.IsFulfilmentExpanded;
    /// <summary>The row's one-line answer: the fulfilment, the phone and the promise together.</summary>
    public string FulfilmentSummary => fulfilment.FulfilmentSummary;

    /// <summary>Title of the block's header row. A constant, so it is shared rather than duplicated.</summary>
    public const string FulfilmentRowTitle = FulfilmentEditor.FulfilmentRowTitle;

    /// <summary>What the header says it will do when tapped.</summary>
    public string FulfilmentToggleHint => fulfilment.FulfilmentToggleHint;

    /// <summary>Counter service or takeaway.</summary>
    public OrderType OrderType => fulfilment.OrderType;

    /// <summary>True while the order is eaten on the premises.</summary>
    public bool IsCounterService => fulfilment.IsCounterService;

    /// <summary>True while the order is taken away.</summary>
    public bool IsTakeaway => fulfilment.IsTakeaway;

    /// <summary>«В зале» or «С собой», as the row reads it.</summary>
    public string OrderTypeText => fulfilment.OrderTypeText;

    /// <summary>The button's caption: the type the order is NOT.</summary>
    public string ToggleOrderTypeText => fulfilment.ToggleOrderTypeText;

    /// <summary>What the button says it will do, including what switching will cost.</summary>
    public string ToggleOrderTypeHint => fulfilment.ToggleOrderTypeHint;

    /// <summary>
    /// The number kept for a takeaway order. Held only while the order is on the cart and written by
    /// <see cref="ICheckoutService"/>, which is the only place that decides to keep one at all.
    /// </summary>
    public string? CustomerPhone => fulfilment.CustomerPhone;

    /// <summary>The number as the cashier reads it back, masked.</summary>
    /// <remarks>
    /// The cart is the customer-facing surface, so the mask goes here and nowhere else on this screen.
    /// </remarks>
    public string PhoneDisplayText => fulfilment.PhoneDisplayText;

    /// <summary>Whether a number has been entered.</summary>
    public bool HasCustomerPhone => fulfilment.HasCustomerPhone;

    /// <summary>True while the row should invite a number: a takeaway with none yet.</summary>
    public bool HasNoCustomerPhone => fulfilment.HasNoCustomerPhone;

    /// <summary>When the customer was told it would be ready, or <c>null</c> for as soon as possible.</summary>
    public DateTimeOffset? RequestedAt => fulfilment.RequestedAt;

    /// <summary>Whether the promised time has already passed.</summary>
    /// <remarks>
    /// A computed reading of the wall clock, not a stored flag, so it cannot disagree with the order —
    /// and therefore goes stale on its own, which is why <c>LoadAsync</c> re-reads it on every return to
    /// the tab.
    /// </remarks>
    public bool IsRequestedTimeLate => fulfilment.IsRequestedTimeLate;

    /// <summary>What the "когда" control reads: «сейчас», or the chosen clock time.</summary>
    public string RequestedTimeText => fulfilment.RequestedTimeText;

    /// <summary>The promise as the CUSTOMER reads it: a range, or a clock time once one is set.</summary>
    public string PromiseText => fulfilment.PromiseText;

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
