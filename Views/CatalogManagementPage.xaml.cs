using System;
using System.Threading.Tasks;
using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePosApp.Diagnostics;
using CafePos.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp.Views;

/// <summary>
/// Catalogue list. Thin code-behind: shows the form sheet (<see cref="CatalogFormsPage"/>)
/// when the ViewModel asks for it and reloads the list after a save.
/// </summary>
public partial class CatalogManagementPage : ContentPage
{
    private readonly CatalogManagementViewModel viewModel;
    private readonly DatabaseBootstrapper bootstrapper;
    private readonly IServiceProvider services;

    /// <summary>
    /// Set while a form is being pushed. A rapid double tap fires the FAB twice, and the event
    /// handler below is async void — a second pass used to push a second modal on top of the
    /// first.
    /// </summary>
    private bool formOpening;

    public CatalogManagementPage(CatalogManagementViewModel viewModel, DatabaseBootstrapper bootstrapper, IServiceProvider services)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
        this.bootstrapper = bootstrapper;
        this.services = services;

        // The two subscriptions below are hooked in OnAppearing, not here — see OnAppearing.
    }

    /// <summary>
    /// Publishes the window width to the ViewModel and re-applies the product grid's column span.
    /// </summary>
    /// <remarks>
    /// The span is assigned to the named <c>GridItemsLayout</c> rather than bound:
    /// <c>ItemsLayout</c> is a <c>BindableObject</c> that lives outside the visual tree, so a
    /// <c>{Binding}</c> on it does not reliably inherit the page's BindingContext and the span
    /// silently stayed at the type's default of 1. The arithmetic itself lives in
    /// <c>CatalogManagementViewModel.ProductColumnSpan</c>.
    /// </remarks>
    private void PublishAvailableWidth()
    {
        var window = Application.Current?.Windows.FirstOrDefault();
        var width = window?.Width ?? 0;
        if (width <= 0) return;

        viewModel.AvailableWidth = width;

        // Explicit WidthRequest on all three rows. An explicit WidthRequest bypasses
        // desired-size negotiation entirely — the width is deterministic, not a by-product of
        // the Auto/*/Fill guessing that overflowed twice. All three are set here so they can
        // never drift apart.
        var capped = Math.Min(width, MaxContentWidth);
        HeaderRow.WidthRequest = capped;
        NoticeRow.WidthRequest = capped;
        ContentGrid.WidthRequest = capped;

        var span = viewModel.ProductColumnSpan;
        if (ProductGridLayout.Span != span) ProductGridLayout.Span = span;
    }

    private const double MaxContentWidth = 1400;

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Subscribe HERE, symmetric with the unsubscribes in OnDisappearing, because a modal push
        // makes this page raise OnDisappearing even though the page itself is never discarded:
        // "pushing a modal page onto the modal navigation stack will result in all visible Shell
        // objects raising the Disappearing event" (Shell lifecycle docs), and popping it raises
        // Appearing again. Subscribing in the constructor and unsubscribing in OnDisappearing was
        // therefore a one-shot: the first "Добавить товар" tap removed the only
        // ShowFormRequested handler, the pop brought the page back without re-adding it, and every
        // later request hit `ShowFormRequested?.Invoke` on an empty delegate — the FAB, the
        // row-tap-to-edit and the row overflow's "Изменить" all went silent after one open.
        // The `-=` first makes a second OnAppearing without an OnDisappearing idempotent, which
        // is the idiom MenuPage already uses for Window.SizeChanged.
        viewModel.ShowFormRequested -= OnShowFormRequested;
        viewModel.ShowFormRequested += OnShowFormRequested;
        ProductsList.SelectionChanged -= OnProductsSelectionChanged;
        ProductsList.SelectionChanged += OnProductsSelectionChanged;

        // The window's own resize notification: the page's is not raised for a Shell-hosted page.
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is not null)
        {
            window.SizeChanged -= OnWindowSizeChanged;
            window.SizeChanged += OnWindowSizeChanged;
        }

        PublishAvailableWidth();
        LogLayoutDiagnostics();

        try
        {
            // Migration guarantee: App.CreateWindow only starts the run, so await it here before
            // the catalogue list queries SQLite. Injected, not looked up: the bootstrapper is a
            // DI singleton, so the page joins the shared run rather than starting a competing one.
            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // async void OnAppearing: an escaping exception would kill the process. The
            // bootstrapper does not latch a failure, so the next appearance retries.
            AppLog.Exception("CatalogManagementPage.OnAppearing", exception);
        }
    }

    /// <summary>
    /// One-shot layout diagnostic. Logs the ACTUAL runtime values of the width cap, the column
    /// span and the FAB position so the layout can be verified against a screenshot instead of
    /// predicted. Called once per appearing (not per resize) so the log stays readable.
    /// The values are only populated after the first layout pass, so the read is deferred via
    /// <see cref="MainThread.BeginInvokeOnMainThread"/>. The whole thing is guarded: a diagnostic
    /// must never take down <see cref="OnAppearing"/>, which is <c>async void</c>.
    /// </summary>
    private void LogLayoutDiagnostics()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    // Wait for the first layout pass to settle — reading Width/Height/X/Y
                    // synchronously in OnAppearing returns zeros.
                    await Task.Delay(300);

                    var window = Application.Current?.Windows.FirstOrDefault();
                    var windowWidth = window?.Width ?? 0;
                    var containerWidth = HeaderRow.Width;
                    var containerHeight = HeaderRow.Height;
                    var contentWidth = ContentGrid.Width;
                    var contentHeight = ContentGrid.Height;
                    var listWidth = ProductsList.Width;
                    var listHeight = ProductsList.Height;
                    var fabX = AddFab.X;
                    var fabY = AddFab.Y;
                    var fabWidth = AddFab.Width;
                    var fabHeight = AddFab.Height;
                    var span = ProductGridLayout.Span;

                    // The heights are the ones that matter for "why is there empty space below the
                    // button". windowH is the window, containerH what the capped stack actually
                    // occupies, listH the viewport the operator can scroll. If containerH is well
                    // under windowH, the stack is not filling the window and the gap under the FAB
                    // is a layout fault, not the FAB's own margin.
                    AppLog.Info(
                        $"[LayoutDiag] window={windowWidth:F1} " +
                        $"container={containerWidth:F1}x{containerHeight:F1} " +
                        $"content={contentWidth:F1}x{contentHeight:F1} " +
                        $"list={listWidth:F1}x{listHeight:F1} " +
                        $"fab=({fabX:F1},{fabY:F1}) {fabWidth:F1}x{fabHeight:F1} " +
                        $"span={span}");

                    // Overflow guard: a silent overflow that a human has to spot in a
                    // screenshot is a failure mode worth making loud.
                    if (containerWidth > windowWidth + 1)
                        AppLog.Warn($"[LayoutDiag] OVERFLOW container={containerWidth:F1} > window={windowWidth:F1}");
                    if (contentWidth > containerWidth + 1)
                        AppLog.Warn($"[LayoutDiag] OVERFLOW content={contentWidth:F1} > container={containerWidth:F1}");
                    if (contentWidth > windowWidth + 1)
                        AppLog.Warn($"[LayoutDiag] OVERFLOW content={contentWidth:F1} > window={windowWidth:F1}");
                    if (listWidth > contentWidth + 1)
                        AppLog.Warn($"[LayoutDiag] OVERFLOW list={listWidth:F1} > content={contentWidth:F1}");
                }
                catch (Exception ex)
                {
                    AppLog.Exception("[LayoutDiag] read", ex);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Exception("[LayoutDiag] schedule", ex);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Paired with the two `-=` lines at the top of OnAppearing. This page is a Shell TabBar
        // ShellContent, so the DataTemplate builds it once and it outlives every appearance; the
        // handlers are still torn down here because a modal push raises this, and re-adding them
        // on the way back in is what keeps the form path alive for the second and later taps.
        viewModel.ShowFormRequested -= OnShowFormRequested;
        ProductsList.SelectionChanged -= OnProductsSelectionChanged;

        // Paired with the Window.SizeChanged subscription in OnAppearing — same symmetry rule.
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is not null) window.SizeChanged -= OnWindowSizeChanged;
    }

    private void OnWindowSizeChanged(object? sender, EventArgs e) => PublishAvailableWidth();

    /// <summary>
    /// A tap on a product row opens its form. The list's SelectionMode is Single, so the selection
    /// is read as "open this product's form" and immediately cleared, which is what lets the same
    /// row be tapped twice in a row.
    ///
    /// This handler used to branch on a bulk-price select mode as well, with SelectionMode
    /// switching to Multiple. That mode is gone by the owner's decision, and with it
    /// IsSelectionMode and ApplySelection; the branch is removed rather than left to throw.
    /// A tap gesture recognizer inside the row template was tried before any of this and dropped:
    /// it competed with the list's own selection handling.
    /// </summary>
    private void OnProductsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not CollectionView list) return;

        if (e.CurrentSelection.Count == 0) return;

        if (e.CurrentSelection[0] is Product product)
            viewModel.EditProductCommand.Execute(product);

        // Cleared synchronously: the form is a modal, and leaving the row highlighted underneath it
        // made it look like the list was in a selection state.
        list.SelectedItem = null;
    }

    private async void OnShowFormRequested(CatalogFormRequest request)
    {
        // Guard against a double tap: two rapid taps would push two modals.
        if (formOpening) return;
        formOpening = true;
        try
        {
            // The sheet is only opened from this page, so the first load above already migrated the
            // database; re-await anyway to keep the guarantee local to every query on this page.
            await bootstrapper.InitializeAsync();

            var page = services.GetRequiredService<CatalogFormsPage>();
            await page.OpenAsync(request);
            await Navigation.PushModalAsync(page);

            if (!await page.WaitForCloseAsync()) return;

            await bootstrapper.InitializeAsync();
            await viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // async void: an escaping exception would kill the process.
            AppLog.Exception("CatalogManagementPage.OnShowFormRequested", exception);
        }
        finally
        {
            formOpening = false;
        }
    }
}
