using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePosApp.ViewModels;

namespace CafePosApp.Views;

public partial class MenuPage : ContentPage
{
    private readonly MenuViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;

    public MenuPage(MenuViewModel viewModel, DatabaseBootstrapper bootstrapper)
    {
        AppLog.Info("MenuPage constructor started");
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
        this.bootstrapper = bootstrapper;

        // The window is the size source, not the page. A Shell-hosted ContentPage on Windows never
        // gets OnSizeAllocated, and its own VisualElement.Width stays 0 through a whole session of
        // resizes — both measured, not assumed. Window.Width is the one number that is populated,
        // and it is what the catalogue action sheet already sizes itself from (see
        // CatalogActionSheetPopup.SizeToWindow). Hooked in OnAppearing rather than the constructor
        // because Application.Current.Windows is only populated once the window exists.
    }

    /// <summary>
    /// Publishes the window width to the ViewModel and re-applies the product grid's column span.
    /// </summary>
    /// <remarks>
    /// The span is assigned to the named <c>GridItemsLayout</c> rather than bound:
    /// <c>ItemsLayout</c> is a <c>BindableObject</c> that lives outside the visual tree, so a
    /// <c>{Binding}</c> on it does not reliably inherit the page's BindingContext and the span
    /// silently stayed at the type's default of 1. The arithmetic itself lives in
    /// <c>MenuViewModel.ProductColumnSpan</c>.
    /// </remarks>
    private void PublishAvailableWidth()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        var width = window?.Width ?? 0;
        if (width <= 0) return;

        viewModel.AvailableWidth = width;

        var span = viewModel.ProductColumnSpan;
        if (ProductGridLayout.Span != span) ProductGridLayout.Span = span;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // The window's own resize notification: the page's is not raised for a Shell-hosted page.
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is not null)
        {
            window.SizeChanged -= OnWindowSizeChanged;
            window.SizeChanged += OnWindowSizeChanged;
        }

        PublishAvailableWidth();

        try
        {
            // Startup (App.CreateWindow) only *starts* the migration so the window is not held
            // up, so this await is what actually guarantees a migrated schema before the first
            // query. The bootstrapper is kept in the constructor for that reason: dropping it
            // would leave this page racing App's fire-and-forget trigger. It is the same DI
            // singleton every other page joins, not a second initialization.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            AppLog.Exception("MenuPage.OnAppearing", exception);
        }
    }

    private void OnWindowSizeChanged(object? sender, EventArgs e) => PublishAvailableWidth();
}

