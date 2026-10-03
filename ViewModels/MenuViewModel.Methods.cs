using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out of
// the change that added it; it belongs beside ResourceStyles.TryGetColor. See its own remarks.
using CafePosApp.Converters;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

/// <summary>
/// Menu and cart. The cart is autosaved as a draft (crash safe) and can be parked;
/// the order itself is created by <see cref="ICheckoutService"/> in one transaction.
/// </summary>
public partial class MenuViewModel : ObservableObject
{
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly ICatalogService catalog;
    private readonly ICheckoutService checkout;
    private readonly IDraftOrderService drafts;
    private readonly IInventoryService inventory;
    private readonly IModifierPicker modifierPicker;
    private readonly IVariantPicker variantPicker;
    private readonly IDraftPicker draftPicker;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly IPaymentSheet paymentSheet;
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
        ICheckoutService checkout,
        IDraftOrderService drafts,
        IInventoryService inventory,
        IModifierPicker modifierPicker,
        IVariantPicker variantPicker,
        IDraftPicker draftPicker,
        IDialogService dialogs,
        IHapticService haptics,
        IPaymentSheet paymentSheet,
        INavigationService navigation,
        IShiftSession shiftSession,
        TimeProvider timeProvider,
        ILogger<MenuViewModel> logger)
    {
        this.catalog = catalog;
        this.checkout = checkout;
        this.drafts = drafts;
        this.inventory = inventory;
        this.modifierPicker = modifierPicker;
        this.variantPicker = variantPicker;
        this.draftPicker = draftPicker;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.paymentSheet = paymentSheet;
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
        SelectCategoryCommand = new RelayCommand<CategoryMenuItemViewModel?>(SelectCategory);
        AddItemCommand = new RelayCommand<CartItemViewModel>(AddItem);
        RemoveItemCommand = new RelayCommand<CartItemViewModel>(RemoveItem);
        PayAndCreateCommand = new AsyncRelayCommand(PayAndCreateAsync);
        CreateWithoutPaymentCommand = new AsyncRelayCommand(CreateWithoutPaymentAsync);
        ParkOrderCommand = new AsyncRelayCommand(ParkOrderAsync);
        OpenParkedCommand = new AsyncRelayCommand(OpenParkedAsync);
    }

    public ObservableCollection<Product> Products { get; } = [];
    public ObservableCollection<Product> FilteredProducts { get; } = [];
    public ObservableCollection<CategoryMenuItemViewModel> Categories { get; } = [];
    public ObservableCollection<CartItemViewModel> Cart { get; } = [];

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
    /// The cart total. Setting it raises <see cref="TotalText"/> as well, because the cart footer
    /// binds the formatted amount rather than the decimal.
    /// </summary>
    public decimal Total
    {
        get => total;
        private set
        {
            if (SetProperty(ref total, value)) OnPropertyChanged(nameof(TotalText));
        }
    }

    /// <summary>The cart total in the active currency, e.g. "740,00 ₿".</summary>
    public string TotalText => TextFormat.Money(Total);

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
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

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
        ? ThemeColors.Resolve("Danger", "DangerDark")
        : ThemeColors.Resolve("Success", "SuccessDark");

    /// <summary>Sets a failure message and flags it as one.</summary>
    private void SetError(Exception exception, string prefix) =>
        (Message, IsErrorMessage) = (UserMessages.Describe(exception, prefix), true);

    private Category? selectedCategory;
    public Category? SelectedCategory { get => selectedCategory; private set => SetProperty(ref selectedCategory, value); }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<Product> AddProductCommand { get; }
    public IRelayCommand<CategoryMenuItemViewModel?> SelectCategoryCommand { get; }
    public IRelayCommand<CartItemViewModel> AddItemCommand { get; }
    public IRelayCommand<CartItemViewModel> RemoveItemCommand { get; }
    public IAsyncRelayCommand PayAndCreateCommand { get; }
    public IAsyncRelayCommand CreateWithoutPaymentCommand { get; }
    public IAsyncRelayCommand ParkOrderCommand { get; }
    public IAsyncRelayCommand OpenParkedCommand { get; }
}
