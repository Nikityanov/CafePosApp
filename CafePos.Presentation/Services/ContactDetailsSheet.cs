namespace CafePos.Presentation.Services;

// ── The «Дописать» sheet's CONTRACT ──────────────────────────────────────────────
// Two records and one interface, none of which touches MAUI. They sit here rather than in the
// MAUI project beside the Popup that implements them because OrderDetailsViewModel needs them, and
// the ViewModels are in this project — a seam whose interface lived with its implementation could
// not be named from the other side of the boundary.

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
