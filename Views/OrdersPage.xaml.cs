using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePosApp.ViewModels;

namespace CafePosApp.Views;

public partial class OrdersPage : ContentPage
{
    private readonly OrdersViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public OrdersPage(OrdersViewModel viewModel, DatabaseBootstrapper bootstrapper)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
        this.bootstrapper = bootstrapper;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Subscribed here and released in OnDisappearing rather than in the constructor: the
        // view model outlives the page, so a constructor subscription would stack up one handler
        // per visit and scroll the list once per stale subscription.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        try
        {
            // App.CreateWindow only *starts* the migration; this await is the migration guarantee
            // for the orders board. Injected rather than looked up: the bootstrapper is a DI
            // singleton, so the page joins the same shared run as every other page.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // OnAppearing is async void, so an escaping exception would take the process down.
            // A failed run is not latched by the bootstrapper, so the next appearance retries.
            AppLog.Exception("OrdersPage.OnAppearing", exception);
            return;
        }

        // The board is the operator's main screen: it has to pick up orders placed on another
        // device. StartAutoRefresh was never wired up, so the "интервал обновления" setting
        // in Settings had no effect at all. Only start polling once the first load has landed,
        // otherwise the timer would query an un-migrated database straight away.
        viewModel.StartAutoRefresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        viewModel.StopAutoRefresh();
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Puts the list back at the top when the section filter changes.
    /// </summary>
    /// <remarks>
    /// Only on a filter change, never on the auto-refresh reloads. Changing the filter swaps the
    /// whole contents of the list, and CollectionView keeps the previous scroll offset, so
    /// switching from "Все" to one section could otherwise land the operator halfway down a
    /// different set of orders. The periodic reload is deliberately left alone: yanking the list
    /// back to the top every few seconds while someone is reading a card would be worse than the
    /// stale offset it fixes.
    /// </remarks>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OrdersViewModel.ActiveFilter)) return;

        try
        {
            OrdersList.ScrollTo(0, animate: false);
        }
        catch (Exception exception)
        {
            AppLog.Exception("OrdersPage.OnViewModelPropertyChanged", exception);
        }
    }
}
