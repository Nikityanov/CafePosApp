using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePosApp.ViewModels;

namespace CafePosApp.Views;

public partial class ShiftReportPage : ContentPage
{
    private readonly ShiftReportViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public ShiftReportPage(ShiftReportViewModel viewModel, DatabaseBootstrapper bootstrapper)
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
            // the report queries SQLite. Same DI singleton as every other page.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // async void OnAppearing: an escaping exception would kill the process. The
            // bootstrapper does not latch a failure, so the next appearance retries.
            AppLog.Exception("ShiftReportPage.OnAppearing", exception);
        }
    }
}
