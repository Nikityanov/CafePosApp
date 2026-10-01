using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Services;

/// <summary>
/// Shows the payment bottom sheet. Same mechanism as <see cref="CatalogActionSheet"/> — a toolkit
/// <c>Popup</c> with a dimmed, tappable backdrop and rounded top corners — so the payment sheet
/// is the one sheet the whole app uses, not a second presentation path.
/// </summary>
public sealed class PaymentSheet : IPaymentSheet
{
    public async Task<PaymentSheetResult?> CollectAsync(PaymentSheetRequest request, CancellationToken cancellationToken = default)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new PaymentSheetPopup(request);

        // Same options as CatalogActionSheet: the dim comes from PageOverlayColor (the toolkit
        // insets popup content by 15dp per side, so an in-popup backdrop leaves the page undimmed
        // down both edges) and the rounded top corners come from the shape.
        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<PaymentSheetResult?>(page, popup, options, cancellationToken);
        return result.Result;
    }
}
