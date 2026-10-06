using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

using CafePosApp.Services;
using CafePos.Presentation.Services;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// The «Дописать» sheet: one place to add a phone and a promised time to an order that has already been
/// paid for, because the customer thought of it after the money changed hands.
/// </summary>
/// <remarks>
/// This class owns presentation and nothing else, exactly as <see cref="PaymentSheet"/> does — same
/// toolkit Popup, same dim, same rounded top corners — so adding a phone to a paid order does not
/// introduce a second way this app shows a bottom sheet.
/// <para>
/// It is NOT where the phone contour lives. Whether a number may be stored is decided in Core, by
/// <c>ContactPhoneRule</c> and by <c>IOrderService.AddContactDetailsAsync</c>. The sheet's
/// <c>PromoteToTakeaway</c> is a RECORD OF WHAT THE OPERATOR AGREED TO, not a decision: a view that
/// could decide that would put 152-ФЗ ст. 6(1)(5) in a file with no test over it.
/// </para>
/// </remarks>
public sealed class ContactDetailsSheet : IContactDetailsSheet
{
    private readonly IOrderTimePicker timePicker;

    public ContactDetailsSheet(IOrderTimePicker timePicker)
    {
        this.timePicker = timePicker;
    }

    public async Task<ContactDetailsSheetResult?> ShowAsync(
        ContactDetailsSheetRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Shell.Current?.CurrentPage
            ?? Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return null;

        var popup = new ContactDetailsSheetPopup(request, timePicker);

        // Same options as PaymentSheet and CatalogActionSheet, for the reason they record: the toolkit
        // insets popup content by 15dp per side, so the dim has to come from PageOverlayColor or the
        // page stays undimmed down both edges.
        var options = new PopupOptions
        {
            PageOverlayColor = Color.FromRgba(0, 0, 0, 0.5),
            CanBeDismissedByTappingOutsideOfPopup = true,
            Shape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) }
        };

        var result = await PopupExtensions.ShowPopupAsync<ContactDetailsSheetResult?>(page, popup, options, cancellationToken);
        return result.Result;
    }
}

/// <summary>

