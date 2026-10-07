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

/// <summary>Menu and cart. The cart is autosaved as a draft (crash safe) and can be parked; the order itself is created by in one transaction.</summary>

public partial class MenuViewModel : ObservableObject
{
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
    /// <summary>The autosave gate and the pending cancellation moved to DraftAutosave, with the reason for the gate: two autosaves must never write the active-cart row concurre...</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>


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

        /// <summary>The three Collaborators are built HERE, before anything reads them, and that position is load-bearing twice over.</summary>
        /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

        menu = new MenuCatalogue(catalog, combos, timeProvider);
        fulfilment = new FulfilmentEditor(timeProvider);
        cart = new CartBuilder();
        autosave = new DraftAutosave(drafts, cart, logger);

        // The last two, built here for the same reason as the others: the command lambdas below close
        // over them. The sale takes IsBusy as a delegate rather than reading it, because IsBusy is the
        // shell's - it disables the checkout button, which the shell owns.
        checkoutCoordinator = new CheckoutCoordinator(
            checkout,
            paymentSheet,
            inventory,
            shiftSession,
            dialogs,
            cart,
            haptics,
            logger,
            message => Message = message,
            message => (Message, IsErrorMessage) = (message, true),
            busy => IsBusy = busy);

        // Parking has no navigation and no busy flag, so nine dependencies against the sale's eleven.
        parkedCarts = new ParkedCarts(
            drafts,
            draftPicker,
            dialogs,
            timeProvider,
            cart,
            haptics,
            logger,
            message => Message = message,
            message => (Message, IsErrorMessage) = (message, true));

        /// <summary>The resolver speaks through the shell's two channels rather than owning them: `announce` is `Message`, whose setter retires a pending undo, and `sayError` is Se...</summary>
        /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

        composition = new CompositionResolver(
            combos,
            comboEditor,
            cart,
            haptics,
            logger,
            message => Message = message,
            message =>
            {
                Message = message;
                IsErrorMessage = true;
            });

        /// <summary>cart.Changed, NOT Cart.CollectionChanged.</summary>
        /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

        cart.Changed += () =>
        {
            OnPropertyChanged(nameof(CanCreateOrder));
            autosave.Schedule();
        };

        // The editor owns the state; the shell owns the bindings. Re-announcing on every change is
        // what keeps a bound row reading the editor's value rather than a stale copy, and it is the
        // reason every proxy below is a plain read instead of a mirrored field.
        fulfilment.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        autosave.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
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
        AddComboCommand = new AsyncRelayCommand<MenuComboViewModel>(composition.AddComboAsync);
        SelectCategoryCommand = new RelayCommand<CategoryMenuItemViewModel?>(SelectCategory);
        AddItemCommand = new RelayCommand<CartItemViewModel>(AddItem);
        RemoveItemCommand = new RelayCommand<CartItemViewModel>(RemoveItem);
        EditLinePriceCommand = new AsyncRelayCommand<CartItemViewModel>(EditLinePriceAsync);
        EditLineCompositionCommand = new AsyncRelayCommand<CartItemViewModel>(composition.EditLineCompositionAsync);
        ToggleOrderTypeCommand = new RelayCommand(SwitchOrderType);
        EditPhoneCommand = new AsyncRelayCommand(EditPhoneAsync);
        ToggleFulfilmentCommand = new RelayCommand(() => fulfilment.SetExpanded(!IsFulfilmentExpanded));
        UndoRemoveCommand = new RelayCommand(UndoRemove);
        PickTimeCommand = new AsyncRelayCommand(PickTimeAsync);
        PayAndCreateCommand = new AsyncRelayCommand(PayAndCreateAsync);
        ParkOrderCommand = new AsyncRelayCommand(ParkOrderAsync);
        OpenParkedCommand = new AsyncRelayCommand(OpenParkedAsync);


        /// <summary>The cart owns the total now, but the button that QUOTES it is the shell's, so the shell has to hear about the total moving.</summary>
        /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

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

    /// <summary>The menu board: dishes, bundle tiles and the category strip, with the filter over all three.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private readonly MenuCatalogue menu;

    /// <summary>The cart, its total and its undo. One of the six Collaborators, and the only one with no dependencies of its own.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private readonly CartBuilder cart;

    /// <summary>Everything about putting a bundle on the cart. The fourth Collaborator.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    private readonly CompositionResolver composition;

    /// <summary>The draft: the header notice, the debounced write of the cart, and the restore of a saved one. The fifth Collaborator.</summary>

    private readonly DraftAutosave autosave;

    /// <summary>The sale: pre-flight the stock, take the money, book the order.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    private readonly CheckoutCoordinator checkoutCoordinator;

    /// <summary>Parking a receipt under a name, and picking one back up.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    private readonly ParkedCarts parkedCarts;

    /// <summary>The cart's lines, in the order the operator read them.</summary>
    public ObservableCollection<CartItemViewModel> Cart => cart.Cart;

    public decimal Total => cart.Total;

    /// <summary>Every dish the till knows, before filtering.</summary>
    public ObservableCollection<Product> Products => menu.Products;

    /// <summary>The dishes on the grid: the selected category's, inside their time window.</summary>
    public ObservableCollection<Product> FilteredProducts => menu.FilteredProducts;

    /// <summary>The category strip, «Все» first and «Комбо» last.</summary>
    public ObservableCollection<CategoryMenuItemViewModel> Categories => menu.Categories;

    /// <summary>The bundles on the menu board, as their own row rather than as more entries in .</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    public ObservableCollection<MenuComboViewModel> Combos => menu.Combos;

    /// <summary>── Product grid width contract ────────────────────────────────────────────────────────────────── The two-column grid clipped on narrow windows, and the fix had...</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    private const double PageHorizontalPadding = 32;      // Grid Padding="16", both sides
    private const int ProductColumns = 2;                 // GridItemsLayout Span when there is room
    private const double ProductColumnSpacing = 4;        // GridItemsLayout HorizontalItemSpacing
    private const double MinimumProductCardWidth = 140;   // below this a two-up card is cramped

    /// <summary>Span before the first layout pass has reported a width.</summary>
    private const int DefaultProductColumnSpan = ProductColumns;

    private double availableWidth;

    /// <summary>The window's width in device independent pixels, pushed in from Views.MenuPage whenever it is resized. Feeds ; not bound from XAML.</summary>

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

    /// <summary>    /// <summary>Product cards per row: two while each column would still be at least`MinimumProductCardWidth` wide, one below that.</summary></summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>`MinimumProductCardWidth` wide, one below that.</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

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

    /// <summary>The checkout button's caption, which is the ACTION and the AMOUNT it will charge: «Оплатить 220.00 ₽».</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

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

            /// <summary>…and it retires the pending undo.</summary>
            /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

            ClearPendingUndo();
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <remarks>`docs/decisions/menu.md`</remarks>

    public string DraftNotice => autosave.DraftNotice;

    /// <summary>Whether the header shows the notice instead of the word «Корзина».</summary>
    public bool HasDraftNotice => autosave.HasDraftNotice;

    /// <summary>── Undo of a removal ─────────────────────────────────────────────────────────────────────── Baymard's five requirements for a cart include "provide an undo opt...</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>


    /// <remarks>`docs/decisions/menu.md`</remarks>

    public bool HasUndo => cart.HasUndo;

    /// <summary>Retires a pending undo, as <c>Message</c>'s setter does for every message.</summary>
    private void ClearPendingUndo() => cart.ClearPendingUndo();

    /// <remarks>`docs/decisions/menu.md`</remarks>

    private void UndoRemove()
    {
        var restored = cart.Restore();
        if (restored is null) return;

        Message = $"{restored}— возвращено в корзину.";
        haptics.Click();
    }


    private bool isErrorMessage;
    /// <summary>Whether `Message` reports a failure.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

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

    /// <summary>The ink for : red on a failure, green otherwise.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    public Color MessageColor => IsErrorMessage
        ? PaletteAccess.Resolve("Danger", "DangerDark")
        : PaletteAccess.Resolve("Success", "SuccessDark");

    /// <summary>Sets a failure message and flags it as one.</summary>
    private void SetError(Exception exception, string prefix) =>
        (Message, IsErrorMessage) = (UserMessages.Describe(exception, prefix), true);

    /// <summary>The chosen category, or <c>null</c> for everything. The catalogue's, computed.</summary>
    public Category? SelectedCategory => menu.SelectedCategory;

    /// <summary>── Which chip is selected ────────────────────────────────────────────────────────────────── A KEY, not a Category, and it belongs to the catalogue now.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    public Guid SelectedFilterKey => menu.SelectedFilterKey;

    /// <summary>True while the «Комбо» chip is selected.</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    public bool IsCombosOnly => menu.IsBundlesOnly;
    /// <summary>─ Fulfilment, contact and time ──────────────────────────────────────────────────────────────────────────────────── Three facts about the ORDER rather than abou...</summary>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>


    /// <remarks>`docs/decisions/menu.md`</remarks>

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

    /// <summary>The number kept for a takeaway order. Held only while the order is on the cart and written by , which is the only place that decides to keep one at all.</summary>

    public string? CustomerPhone => fulfilment.CustomerPhone;

    /// <remarks>`docs/decisions/menu.md`</remarks>

    public string PhoneDisplayText => fulfilment.PhoneDisplayText;

    /// <summary>Whether a number has been entered.</summary>
    public bool HasCustomerPhone => fulfilment.HasCustomerPhone;

    /// <summary>True while the row should invite a number: a takeaway with none yet.</summary>
    public bool HasNoCustomerPhone => fulfilment.HasNoCustomerPhone;

    /// <summary>When the customer was told it would be ready, or <c>null</c> for as soon as possible.</summary>
    public DateTimeOffset? RequestedAt => fulfilment.RequestedAt;

    /// <remarks>`docs/decisions/menu.md`</remarks>

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

    /// <summary>Flips the order between eating in and taking away, in one tap, from one button.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    public IRelayCommand ToggleOrderTypeCommand { get; }

    /// <summary>Enters a phone, checks it, and has the cashier read it back to the customer.</summary>
    public IAsyncRelayCommand EditPhoneCommand { get; }

    /// <summary>Opens and closes the fulfilment/contact/time block in place.</summary>
    /// <remarks>Почему так — `docs/decisions/menu.md`</remarks>

    public IRelayCommand ToggleFulfilmentCommand { get; }

    /// <summary>Puts back the line the last message removed. See the undo notes near <see cref="HasUndo"/>.</summary>
    public IRelayCommand UndoRemoveCommand { get; }

    /// <summary>Opens the "когда" sheet: as soon as possible, or a clock time.</summary>
    public IAsyncRelayCommand PickTimeCommand { get; }

    /// <summary>    /// <summary>Takes the payment and books the order paid — or books it to be paid on collection, which theoperator chooses inside the sheet.</summa...</summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>operator chooses inside the sheet.</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/menu.md`</remarks>

    public IAsyncRelayCommand PayAndCreateCommand { get; }

    public IAsyncRelayCommand ParkOrderCommand { get; }
    public IAsyncRelayCommand OpenParkedCommand { get; }
}
