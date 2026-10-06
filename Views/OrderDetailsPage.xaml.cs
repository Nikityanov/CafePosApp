using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePos.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp.Views;

public partial class OrderDetailsPage : ContentPage, IQueryAttributable
{
    private readonly OrderDetailsViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    // This page is created by the Shell route table (AppShell.xaml.cs registers "order-details"),
    // not by the tab ContentTemplates, so it cannot rely on any other page having appeared first
    // and it is reachable from every tab via Services/Platform/NavigationService.cs.
    //
    // The dependencies are resolved here rather than through constructor injection on purpose:
    // Routing.RegisterRoute(string, Type) builds the page through MAUI's TypeRouteFactory, and
    // that type ships BOTH an ActivatorUtilities-based factory and a plain
    // Activator.CreateInstance one. Which of the two the Shell picks could not be established
    // from the shipped MAUI 10.0.101 assembly, and guessing wrong would turn every navigation
    // to this route into a MissingMethodException on a path no test covers. The container
    // lookup is the resolution strategy already proven to work for this page. Whoever next owns
    // AppShell.xaml.cs / MauiProgram.cs can register a service-aware factory and the
    // constructor below can then take (OrderDetailsViewModel, DatabaseBootstrapper).
    public OrderDetailsPage()
    {
        InitializeComponent();
        var services = Application.Current?.Handler?.MauiContext?.Services
            ?? throw new InvalidOperationException("Сервисы приложения недоступны.");
        viewModel = services.GetRequiredService<OrderDetailsViewModel>();
        bootstrapper = services.GetRequiredService<DatabaseBootstrapper>();
        BindingContext = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        // Shell applies query attributes as soon as the route resolves — BEFORE OnAppearing — and
        // the ViewModel kicks its load off from here. So the migration guarantee has to sit on
        // this path; an OnAppearing override would run too late to stop the query.
        _ = ApplyQueryAttributesWhenDatabaseReadyAsync(query);
    }

    private async Task ApplyQueryAttributesWhenDatabaseReadyAsync(IDictionary<string, object> query)
    {
        try
        {
            // Same shared run as App startup and the other pages; the bootstrapper collapses
            // concurrent callers onto one initialization.
            await bootstrapper.InitializeAsync();
        }
        catch (Exception exception)
        {
            // A failed run is evicted from the bootstrapper's cache rather than latched, so the
            // next navigation retries instead of leaving the page permanently broken.
            AppLog.Exception("OrderDetailsPage.ApplyQueryAttributes", exception);
            return;
        }

        // The page, not the ViewModel, reads Shell's dictionary. The ViewModel used to implement
        // IQueryAttributable itself, which meant a MAUI interface in a class that now lives in a
        // project referencing only Microsoft.Maui.Graphics. The dictionary is a routing detail; what
        // the ViewModel actually needs is a Guid — so that is what it takes.
        if (!query.TryGetValue("OrderId", out var value) || value is not Guid id) return;

        viewModel.ShowOrder(id);
    }
}
