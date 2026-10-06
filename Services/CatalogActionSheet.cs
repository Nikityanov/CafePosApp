using CafePos.Core.Services;
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
/// Shows the row overflow menu. Same mechanism as the variant/modifier/draft pickers, so it goes
/// through the proven popup path rather than a new overlay.
/// </summary>
public sealed class CatalogActionSheet : ICatalogActionSheet
{
    public async Task<string?> ChooseAsync(string title, IReadOnlyList<CatalogAction> actions, CancellationToken cancellationToken = default)
    {
        if (actions.Count == 0) return null;

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new CatalogActionSheetPopup(title, actions);

        // A dimmed, tappable backdrop and rounded top corners so the sheet reads as a bottom
        // sheet rather than a centred dialog. The shape drives the border's StrokeShape.
        //
        // The dim has to come from PageOverlayColor rather than from a backdrop element inside the
        // popup: the toolkit insets a popup's content by 15dp on each side (measured: popup 411dp
        // wide, its content 381dp), so an in-popup backdrop left the page undimmed down both edges.
        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<string?>(page, popup, options, cancellationToken);
        return result.Result;
    }
}

