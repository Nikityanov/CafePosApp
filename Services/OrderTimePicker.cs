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
/// Shows the "when" sheet. Same mechanism as <see cref="CatalogActionSheet"/> and
/// <see cref="PaymentSheet"/> — a toolkit <c>Popup</c> with a dimmed, tappable backdrop and rounded
/// top corners — so the order-time control is not a fourth presentation path in this app.
/// <para>
/// Named <c>OrderTimePicker</c> and the seam <c>IOrderTimePicker</c> rather than the obvious
/// <c>TimePicker</c>: MAUI already has <c>Microsoft.Maui.ITimePicker</c> and
/// <c>Microsoft.Maui.Controls.TimePicker</c>, and an unqualified <c>ITimePicker</c> in a file with
/// MAUI's implicit usings is an ambiguous reference the compiler refuses. The longer name is what keeps
/// it a one-word fix at every call site instead of an alias.
/// </para>
/// </summary>
public sealed class OrderTimePicker : IOrderTimePicker
{
    public async Task<TimePickResult?> PickAsync(
        string title,
        TimeSpan? initial,
        CancellationToken cancellationToken = default)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new TimePickerPopup(title, initial);

        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<TimePickResult?>(page, popup, options, cancellationToken);
        return result.Result;
    }
}

