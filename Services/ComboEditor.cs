using CafePos.Core.Common;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// Shows the composition sheet for a bundle. Same popup path as the other three sheets, so there is
/// one way a sheet is presented in this app and one set of measured fixes behind it.
/// </summary>
public sealed class ComboEditor : IComboEditor
{
    public async Task<IReadOnlyList<ComboSlotChoice>?> ComposeAsync(
        ComboEditorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nothing to pick from is nothing to decide: the sheet would offer no buttons and a confirm
        // that could never become valid. Treated as a dismissal, which leaves the cart untouched —
        // the same safe reading of "the operator changed their mind" the cancel dialogs use.
        if (request.Options.Count == 0) return null;

        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new ComboEditorPopup(request);

        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<ComboEditorResult?>(page, popup, options, cancellationToken);

        // A dismissal is null, not an empty composition. Handing back "no slots" for a tap on the
        // scrim would drop the bundle off the cart silently, which is the one outcome the seam is
        // shaped to make impossible.
        return result.Result?.Slots;
    }
}

