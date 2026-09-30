using CafePos.Core.Services;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Services;

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
        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(16, 16, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<string?>(page, popup, options, cancellationToken);
        return result.Result;
    }
}
