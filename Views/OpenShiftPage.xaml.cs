using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePos.Presentation.ViewModels;

namespace CafePosApp.Views;

public partial class OpenShiftPage : ContentPage
{
    private readonly OpenShiftViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public OpenShiftPage(OpenShiftViewModel viewModel, DatabaseBootstrapper bootstrapper)
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
            // Same reason as every other page: App.CreateWindow only STARTS the migration run, so
            // this page would otherwise query a table that does not exist yet on a first launch.
            // It matters more here than elsewhere, because this is the page a FIRST launch lands on.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadCommand.ExecuteAsync(null);
        }
        catch (Exception exception)
        {
            // async void OnAppearing: an escaping exception would kill the process. The bootstrapper
            // does not latch a failure, so the next appearance retries.
            AppLog.Exception("OpenShiftPage.OnAppearing", exception);
        }
    }
}
