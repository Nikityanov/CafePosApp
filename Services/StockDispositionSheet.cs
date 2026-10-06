using CafePos.Core.Models;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// Shows the stock disposition of a cancellation. Same toolkit <c>Popup</c> as the payment sheet
/// and the catalogue action sheet, so the choice is presented by the mechanism the app already
/// uses rather than by a second one.
/// </summary>
public sealed class StockDispositionSheet : IStockDispositionSheet
{
    public async Task<StockDisposition?> ChooseAsync(
        StockDispositionSheetRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new StockDispositionSheetPopup(request);

        // Same options as CatalogActionSheet/PaymentSheet: the dim comes from PageOverlayColor
        // (the toolkit insets popup content by 15dp per side, so an in-popup backdrop leaves the
        // page undimmed down both edges) and the rounded top corners come from the shape.
        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<StockDispositionChoice?>(page, popup, options, cancellationToken);
        return result.Result?.Value;
    }
}

