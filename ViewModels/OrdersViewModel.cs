using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

public partial class OrderRowViewModel : ObservableObject
{
    public OrderRowViewModel(Order model, AppSettings settings)
    {
        Model = model;
        OrderTitle = $"{settings.OrderPrefix} #{model.OrderNumber}";
    }

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

    public string NextActionText => Model.Status == OrderStatus.InProgress ? "Готов" : "Закрыть";
    public bool CanAdvance => Model.Status is OrderStatus.InProgress or OrderStatus.Ready;
    public bool CanCancel => Model.Status is OrderStatus.InProgress or OrderStatus.Ready;
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
        ILogger<OrdersViewModel> logger)
    {
        this.orders = orders;
        this.settings = settings;
        this.navigation = navigation;
        this.dialogs = dialogs;
        this.haptics = haptics;
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
    public IRelayCommand<OrderFilterChip> SelectSectionFilterCommand { get; }
}
