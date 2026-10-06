using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePos.Presentation.ViewModels;

namespace CafePosApp.Views;

public partial class ShiftAnalyticsPage : ContentPage
{
    private readonly ShiftAnalyticsViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public ShiftAnalyticsPage(ShiftAnalyticsViewModel viewModel, DatabaseBootstrapper bootstrapper)
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
            // Migration guarantee: App.CreateWindow only starts the run, so await it here before
            // the analytics queries SQLite. Same DI singleton as every other page.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // async void OnAppearing: an escaping exception would kill the process. The
            // bootstrapper does not latch a failure, so the next appearance retries.
            AppLog.Exception("ShiftAnalyticsPage.OnAppearing", exception);
        }
    }
}
