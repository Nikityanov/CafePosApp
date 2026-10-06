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
    }
}
