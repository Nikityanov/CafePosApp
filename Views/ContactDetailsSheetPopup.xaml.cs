using CafePos.Core.Common;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Xaml;

namespace CafePosApp.Views;

/// <summary>
/// The «Дописать» sheet: adds a phone and a promised time to an order that has already been paid for.
/// </summary>
/// <remarks>
/// Sizing, backdrop and shape come from <see cref="ContactDetailsSheet"/>, which copies
/// <see cref="PaymentSheetPopup"/> — this class has no presentation opinions of its own.
/// <para>
/// <b>IT CANNOT WRITE ANYTHING.</b> The sheet returns a <see cref="ContactDetailsSheetResult"/> and the
/// ViewModel hands it to Core, where the phone contour is decided. The confirm button is never disabled
/// for a counter-service order and the sheet never silently flips the fulfilment mode: it RECORDS the
/// operator ticking the box, and Core refuses the write when that tick is missing. A view that decided
/// this for itself would be a view holding 152-ФЗ ст. 6(1)(5).
/// </para>
/// <para>
/// <b>THIS SHEET ADDS AND DOES NOT CLEAR.</b> A null <c>PromisedAt</c> in the result means "leave the
/// promise alone", so choosing «Приготовить как можно скорее» in the clock picker — which would mean
/// clearing a time on a closed sale — is treated as leaving the time field alone instead. Clearing a
/// promise is a different decision with a different risk and it was not asked for; if it is wanted it
/// belongs in its own control, not as a side effect of picking a time.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class ContactDetailsSheetPopup : Popup<ContactDetailsSheetResult?>
{
    private readonly IOrderTimePicker timePicker;

    /// <summary>Whether the order is ALREADY takeaway, so the change-of-mode row is unnecessary.</summary>
    private readonly bool isTakeaway;

    /// <summary>The promise as last set in THIS sheet session, or the order's own.</summary>
    private DateTimeOffset? promisedAt;

    /// <summary>
    /// The phone field's own colour, captured once so a refusal can be undone by putting back exactly
    /// what was there.
    /// </summary>
    /// <remarks>
    /// Not <c>Colors.Default</c>, which does not exist, and not a hard-coded grey, which would be wrong in
    /// one of the two themes and would silently override whatever the field was themed with. Reading the
    /// value back off the control is the only way to restore "normal" without knowing what normal is.
    /// </remarks>
    private Color entryTextColor;

    public ContactDetailsSheetPopup(ContactDetailsSheetRequest request, IOrderTimePicker timePicker)
    {
        InitializeComponent();

        this.timePicker = timePicker;
        isTakeaway = request.IsTakeaway;
        promisedAt = request.PromisedAt;

        TitleLabel.Text = request.Title;
        PhoneEntry.Text = request.Phone ?? string.Empty;
        entryTextColor = PhoneEntry.TextColor;

        RenderTime();
        UpdatePromoteVisibility();
        PhoneEntry.TextChanged += (_, _) => UpdatePromoteVisibility();

        SheetContent.SizeChanged += (_, _) => SizeToWindow();
    }

    /// <summary>
    /// Whether the fulfilment-change row is on screen: a number has been typed AND the order is not
    /// already takeaway. Nothing else, so it is never a box the operator must notice in order to proceed.
    /// </summary>
    private void UpdatePromoteVisibility()
    {
        var hasPhone = !string.IsNullOrWhiteSpace(PhoneEntry.Text);
        PromoteBorder.IsVisible = hasPhone && !isTakeaway;

        if (!PromoteBorder.IsVisible)
        {
            PromoteCheck.IsChecked = false;
            return;
        }

        PromoteLabel.Text = "Заказ в зале — телефон хранить не нужно. "
            + "Поставьте галочку, чтобы заказ стал «С собой».";
    }

    /// <summary>The promise as prose: a named clock time, or the as-soon-as-possible state.</summary>
    private void RenderTime() => TimeLabel.Text = promisedAt is { } promised
        ? ClockTime.Format(promised.ToLocalTime())
        : "Как можно скорее";

    /// <summary>
    /// Opens the app's own clock-time picker over this sheet.
    /// </summary>
    /// <remarks>
    /// A second Popup over a Popup, worth stating plainly: it is safe HERE because both are toolkit
    /// popups on the same page. The crash this codebase records was the SYSTEM dial — MAUI's Android
    /// <c>TimePicker</c> is an EditText that opens a native dialog in another window above a popup,
    /// which is why that sheet uses in-sheet steppers and presets. No platform dialog is involved now.
    /// </remarks>
    private async void OnTimeTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            // DateTimeOffset.TimeOfDay is already a TimeSpan in LOCAL time — it is the wall-clock part of
            // the value, so there is nothing to convert, and asking for a conversion here is the shape of
            // bug this codebase already paid for once (see ClockTime).
            var picked = await timePicker.PickAsync("Когда отдать", promisedAt?.TimeOfDay);
            if (picked is null) return;

            // "As soon as possible" is not a clearing of the promise here — see the class remarks. The
            // field keeps whatever it had, so a tap that ends in that choice leaves the order as it was.
            if (picked.IsAsSoonAsPossible) return;

            // The picker returns a time of DAY; the sheet stores a MOMENT, because that is what
            // Order.RequestedAt is. Tomorrow's day is used, so a request made at 19:00 for «к 14:20»
            // does not land in the past — which the board would then read as overdue.
            var day = (promisedAt ?? DateTimeOffset.Now).ToLocalTime();
            promisedAt = new DateTimeOffset(day.Year, day.Month, day.Day, picked.TimeOfDay.Hours, picked.TimeOfDay.Minutes, 0, day.Offset);

            RenderTime();
        }
        catch (Exception exception)
        {
            AppLog.Exception("ContactDetailsSheetPopup.OnTimeTapped", exception);
        }
    }

    /// <summary>
    /// Toggles the fulfilment-change agreement from anywhere on its row.
    /// </summary>
    /// <remarks>
    /// The whole row is the target because the CheckBox inside it is ~24dp — under the app's own
    /// touch-target floor. A confirmation this important should not be something the operator has to hit
    /// within a few pixels of, and it makes the row addressable by its text in a test rather than by
    /// coordinate arithmetic.
    /// <para>
    /// Assigning <c>IsChecked</c> directly rather than calling <c>Check()</c>/<c>Uncheck()</c>: the
    /// CheckBox is non-interactive so no platform toggle competes with this one, and a test asserting on
    /// its state reads the same field the result carries.
    /// </para>
    /// </remarks>
    private void OnPromoteTapped(object? sender, TappedEventArgs e) =>
        PromoteCheck.IsChecked = PromoteCheck.IsChecked != true;

    private async void OnConfirmClicked(object? sender, EventArgs e)
    {
        var typed = PhoneEntry.Text;
        var phone = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();

        // Normalised HERE as a courtesy, so an obviously wrong number is refused before a round trip —
        // but Core normalises again and ITS answer is the one that counts. Two passes on purpose: this
        // one saves the operator a wait, that one is the rule. The tick is the flow and the number is
        // red on the field, which is where the operator is already looking.
        if (phone is not null && !PhoneNumber.IsValid(PhoneNumber.Normalize(phone)))
        {
            PhoneEntry.TextColor = Microsoft.Maui.Graphics.Colors.IndianRed;
            return;
        }

        PhoneEntry.TextColor = entryTextColor;

        try
        {
            await CloseAsync(new ContactDetailsSheetResult(
                phone, promisedAt, PromoteCheck.IsChecked == true));
        }
        catch (Exception exception)
        {
            AppLog.Exception("ContactDetailsSheetPopup.OnConfirmClicked", exception);
        }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        try
        {
            await CloseAsync(null);
        }
        catch (Exception exception)
        {
            AppLog.Exception("ContactDetailsSheetPopup.OnCancelClicked", exception);
        }
    }

    /// <summary>
    /// Pins the sheet to the width of the page. A Popup sizes to its content, so a two-field form came
    /// out as a narrow card floating mid-screen; copied from PaymentSheetPopup, where the same
    /// correction was measured rather than assumed.
    /// </summary>
    private void SizeToWindow()
    {
        try
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Width is { } width && width > 0)
            {
                SheetContent.WidthRequest = width;
                Sheet.WidthRequest = width;
            }
        }
        catch (Exception exception)
        {
            AppLog.Exception("ContactDetailsSheetPopup.SizeToWindow", exception);
        }
    }
}
