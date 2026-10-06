using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePos.Presentation.ViewModels;

namespace CafePosApp.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public SettingsPage(SettingsViewModel viewModel, DatabaseBootstrapper bootstrapper)
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
            // the backup list (and the settings read) touch SQLite.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadBackupsAsync();
        }
        catch (Exception exception)
        {
            // async void OnAppearing: an escaping exception would kill the process. The
            // bootstrapper does not latch a failure, so the next appearance retries.
            AppLog.Exception("SettingsPage.OnAppearing", exception);
        }
    }
}
