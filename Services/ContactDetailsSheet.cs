using CafePosApp.Views;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Services;

/// <summary>What the sheet needs to open on the order's current state.</summary>
/// <param name="Title">Sheet title.</param>
/// <param name="Phone">
/// The number already stored, or <c>null</c>. Passed back IN so the sheet opens showing what is there
/// rather than an empty field — an operator editing a number must see the one they are replacing.
/// </param>
/// <param name="PromisedAt">
/// The time already promised, or <c>null</c> for «как можно скорее». Same reason: the sheet is an EDIT,
/// and opening on a blank time would let a save look successful while quietly clearing what was there.
/// </param>
/// <param name="IsTakeaway">
/// Whether the order is already takeaway. Passed IN so the sheet asks for the fulfilment change ONLY when
/// it is actually needed — an operator who typed a number on a takeaway order must not be asked to
/// confirm a change that changes nothing.
/// </param>
public sealed record ContactDetailsSheetRequest(
    string Title,
    string? Phone,
    DateTimeOffset? PromisedAt,
    bool IsTakeaway);

/// <summary>
/// What the operator came away with. <c>null</c> fields mean "leave it alone", NOT "clear it" —
/// clearing a stored number is not something this sheet can do, and a sheet that could would be a
/// way to erase personal data off a closed sale with one tap.
/// </summary>
/// <param name="Phone">The normalised number, or <c>null</c> to leave the stored one untouched.</param>
/// <param name="PromisedAt">The promised time, or <c>null</c> to leave it untouched.</param>
/// <param name="PromoteToTakeaway">
/// The operator agreed to change the order from counter service to takeaway because a number was named.
/// The Core refuses a phone on a counter-service order without this, so the sheet can never write one
/// by accident — see <c>ContactPhoneRule</c>.
/// </param>
public sealed record ContactDetailsSheetResult(string? Phone, DateTimeOffset? PromisedAt, bool PromoteToTakeaway);

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
/// The sheet, behind a seam. A seam and not the concrete class because
/// <c>OrderDetailsViewModel</c> takes constructor dependencies and a test must be able to hand it a
/// sheet that returns a number without a Popup, a window and an emulator being involved.
/// </summary>
public interface IContactDetailsSheet
{
    /// <returns>
    /// What the operator chose, or <c>null</c> when the sheet was dismissed — a dismissal, not an answer,
    /// so the caller must not read anything off it.
    /// </returns>
    Task<ContactDetailsSheetResult?> ShowAsync(
        ContactDetailsSheetRequest request,
        CancellationToken cancellationToken = default);
}
