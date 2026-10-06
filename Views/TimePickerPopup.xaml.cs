using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

/// <summary>
/// The "when" sheet: a lead-time promise («сейчас») on one side and a named clock time on the other.
/// </summary>
/// <remarks>
/// <b>THE TWO FACES ARE ONE FIELD.</b> <see cref="mode"/> is the only state here; the steppers and the
/// «Сейчас» button are two ways of setting it. That is the difference from a checkbox beside a dial,
/// where the two can disagree — a box ticked with the clock still showing 09:00 is an order promised
/// two different times, and nothing on the sheet says which one won.
/// <para>
/// The customer never sees this sheet and never sees a clock time derived from it: the till's promise
/// is a range (see MenuViewModel.PromiseText), because a point estimate is rated worse than a range
/// even when it equals the range's upper bound. What is picked here is the ORDER's
/// <see cref="Order.RequestedAt"/>, and only an order whose promise is already fixed can carry it.
/// </para>
/// <para>
/// <b>WHY THERE IS NO TimePicker ON THIS SHEET.</b> It was one, and it was the crash reported as
/// «выбор ко времени вылетает». Two separate faults, both measured on the emulator:
/// <list type="number">
/// <item>
/// MAUI's Android <see cref="TimePicker"/> is an EditText that opens the SYSTEM dial in a second
/// window above this Popup. So the sheet's entire input path ran through a native dialog stealing
/// focus from a toolkit Popup — two windows, either of which could take the sheet with it.
/// </item>
/// <item>
/// The dialog's <c>onTimeSet</c> callback assigns <c>TimePicker.Time</c>, which raises
/// <c>PropertyChanged</c>, which ran <see cref="SetMode"/>, which built the confirm caption with
/// <c>TimeSpan.ToString("HH:mm")</c>. That format string throws <see cref="FormatException"/> —
/// measured — on the UI thread, inside an event handler, so the process died the instant the operator
/// chose anything but «сейчас». Pressing «К выбранному времени» reached it too, with no dial involved.
/// </item>
/// </list>
/// In-sheet steppers and presets have no second window and no <c>TimeSpan</c> format string, so the
/// whole class of failure is gone rather than narrowed. The steppers wear the app's own 48dp
/// <c>PaymentMethodButton</c> rather than the cart's <c>StepperButton</c>, whose fixed 48dp width is
/// sized for a quantity column and would leave four of them with nothing between them.
/// </para>
/// <para>
/// Sizing, backdrop and shape are copied from <see cref="PaymentSheetPopup"/> rather than invented: a
/// ScrollView inside a Popup fills whatever height it is offered, so <c>VerticalOptions="Start"</c>
/// plus an explicit width is what makes this a sheet and not a panel.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class TimePickerPopup : Popup<TimePickResult?>
{
    /// <summary>Whether the promise is the lead time or a clock time the customer named.</summary>
    private enum Mode
    {
        AsSoonAsPossible,
        NamedTime
    }

    private Mode mode;

    /// <summary>
    /// The grid every time on this sheet lands on: the minute stepper's step, and the grid the seed
    /// and the presets snap to.
    /// </summary>
    /// <remarks>
    /// Five minutes, because a coffee shop does not need 14:22 and a one-minute stepper would need
    /// 105 taps to cross two hours. The cost is that a minute which is not a multiple of five cannot
    /// be chosen here — which is also why <see cref="ClockTime.SnapUp"/> rounds every value the sheet
    /// produces UP onto this grid rather than to nearest: a promise about when something will be ready
    /// must never be rounded back to a moment that has already gone.
    /// </remarks>
    private const int MinuteGrid = 5;

    private TimeSpan chosen;

    public TimePickerPopup(string title, TimeSpan? initial)
    {
        InitializeComponent();
        SizeToWindow();
        SizeChanged += (_, _) => SizeToWindow();

        TitleLabel.Text = title;
        AsapNoteLabel.Text = $"«Сейчас» — это примерно {TextFormat.Duration(Order.LeadTimeMinutes)}, "
                             + "сколько обычно готовится заказ без выбранного времени.";

        // A dismissed sheet leaves the order exactly as it was, so an unopened sheet must not have
        // moved anything either — the sheet animates in from below like the payment sheet does.
        TranslationY = 1000;

        // Opens on the CURRENT state, not on a blank form. A scheduled order shows its own time, so the
        // sheet is a statement about the order rather than a question about it.
        //
        // An ASAP order is seeded with the lead-time promise itself, NOT with "now". It used to open on
        // the current time, which made «К выбранному времени» a foot-gun: the displayed value was a
        // moment that had already begun, so committing it produced an order promised for the past. The
        // two faces then say the same thing, which is what makes them one choice.
        //
        // The seed is snapped up onto the minute grid so the minute stepper can always move off it.
        // An existing named time is NOT snapped — it is the order's own state, and rounding it would
        // quietly rewrite a time the customer already agreed.
        chosen = initial is { } existing
            ? existing
            : ClockTime.NowPlus(Order.LeadTimeMinutes, MinuteGrid);

        SetMode(initial.HasValue ? Mode.NamedTime : Mode.AsSoonAsPossible);

        Opened += (_, _) =>
        {
            SizeToWindow();
            // Cancelled first: TranslateToAsync returns false when another animation takes the
            // property, and the sheet is created fresh per PickAsync so a leftover animation can only
            // be this sheet's own — but leaving one running would fight the one started on the line
            // below and the sheet would stop somewhere on the way in.
            this.CancelAnimations();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    private void OnHourDown(object? sender, EventArgs e) => Step(hours: -1);

    private void OnHourUp(object? sender, EventArgs e) => Step(hours: +1);

    private void OnMinuteDown(object? sender, EventArgs e) => Step(minutes: -MinuteGrid);

    private void OnMinuteUp(object? sender, EventArgs e) => Step(minutes: +MinuteGrid);

    /// <summary>
    /// «+15 мин» / «+30 мин» / «+1 час» — the times a counter is actually asked for, in one tap.
    /// </summary>
    /// <remarks>
    /// Counted from NOW and not from the displayed time, because that is what «через полчаса» means to
    /// whoever said it; a preset that moved relative to whatever the operator had already dialled in
    /// would need its own label stating which, and three such labels are worse than three taps on the
    /// stepper. Snapped up onto the grid for the same reason the seed is: a preset must never land on a
    /// time the minute stepper cannot also reach, and never on "now".
    /// </remarks>
    private void OnPresetClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string parameter }) return;
        if (!int.TryParse(parameter, out var minutes)) return;

        SetTime(ClockTime.NowPlus(minutes, MinuteGrid));
    }

    /// <summary>
    /// Moves <see cref="chosen"/> by whole hours and minutes, wrapping at midnight.
    /// </summary>
    /// <remarks>
    /// Wrapping rather than clamping, on purpose. Clamping at the ends would make «−1 ч» do nothing at
    /// 00:00 and «−5 мин» do nothing at 00:00 — a control that is present, looks live, and is not,
    /// which is the shape .opencode-rules.md refuses everywhere else. A time is a position on a 24-hour
    /// circle and the circle has no ends.
    /// </remarks>
    private void Step(int hours = 0, int minutes = 0)
    {
        var delta = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        var total = chosen.Ticks + delta.Ticks;

        SetTime(TimeSpan.FromTicks(((total % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay));
    }

    /// <summary>
    /// Takes a new value and, because choosing a time IS choosing a time, makes it the named face.
    /// </summary>
    /// <remarks>
    /// This replaces the old <c>Clock.PropertyChanged</c> subscription, which reached the same conclusion
    /// by a much longer road — through the system dialog, through <c>TimePicker.Time</c>'s property
    /// notification, and into the crash documented on the class.
    /// </remarks>
    private void SetTime(TimeSpan value)
    {
        chosen = value;
        SetMode(Mode.NamedTime);
    }

    private void OnAsapClicked(object? sender, EventArgs e) => SetMode(Mode.AsSoonAsPossible);

    private void OnNamedTimeClicked(object? sender, EventArgs e) => SetMode(Mode.NamedTime);

    private void OnConfirmClicked(object? sender, EventArgs e) =>
        _ = CloseAsync(mode == Mode.AsSoonAsPossible
            ? TimePickResult.AsSoonAsPossible
            : TimePickResult.At(chosen));

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);

    /// <summary>
    /// Paints the selected face and the value the confirm will save.
    /// </summary>
    /// <remarks>
    /// The two buttons wear the same active/inactive style swap the payment sheet's cash/card row
    /// uses, because that is this app's one already-measured shape for "choose one of two".
    /// <para>
    /// Every clock reading here goes through <see cref="ClockTime"/>. That is not tidiness: the
    /// original caption formatted a <see cref="TimeSpan"/> with «HH:mm», which throws, and this method
    /// is the first thing the «К выбранному времени» button runs.
    /// </para>
    /// </remarks>
    private void SetMode(Mode value)
    {
        mode = value;

        var active = (Style)Application.Current!.Resources["PaymentMethodActive"]!;
        var inactive = (Style)Application.Current!.Resources["PaymentMethodButton"]!;

        var asapSelected = value == Mode.AsSoonAsPossible;
        AsapButton.Style = asapSelected ? active : inactive;
        NamedTimeButton.Style = asapSelected ? inactive : active;

        ChosenLabel.Text = ClockTime.Format(chosen);

        // The confirm button says what it will save. "Готово" on its own leaves the operator to
        // remember what the sheet was set to; naming the outcome is the same discipline the refund
        // sheet follows ("Вернуть оплату"), applied to a time.
        var label = asapSelected
            ? "Готово · сейчас"
            : $"Готово · к {ClockTime.Format(chosen)}";
        ConfirmButton.Text = label;
        SemanticProperties.SetDescription(
            ConfirmButton,
            asapSelected ? "Приготовить как можно скорее" : $"Приготовить к {ClockTime.Format(chosen)}");

        // A time that has already passed today is an OVERDUE order, and an overdue order is a real
        // order — a customer is waiting past the moment they said they would come back. So it is
        // offered, confirmed like any other, and MARKED here rather than refused: the warning is what
        // stops it being an accident, and refusing it would mean the cashier could not write down what
        // is actually true. MenuViewModel.ResolveRequestedTime books it on today's date for the same
        // reason, so the promise really is in the past.
        var late = !asapSelected && chosen <= DateTime.Now.TimeOfDay;
        LateNoteLabel.IsVisible = late;
        if (late) LateNoteLabel.Text = "Это время уже прошло — заказ будет просроченным.";
    }

    /// <summary>
    /// Matches the sheet to the window it is shown over, and lifts its content above the system
    /// navigation bar. Copied from <see cref="PaymentSheetPopup.SizeToWindow"/> — see its remarks for
    /// why each step is here (the toolkit rewrites <c>HorizontalOptions.Fill</c> to
    /// <c>Center</c>, a ScrollView fills the height it is offered, and an edge-to-edge Android window
    /// covers its own bottom edge).
    /// </summary>
    /// <remarks>
    /// <b>Every write here is COMPARED BEFORE IT IS MADE, and that is what keeps
    /// <c>SizeChanged += SizeToWindow</c> out of trouble.</b> A <c>SizeChanged</c> handler that sets a
    /// size re-raises <c>SizeChanged</c>, so the loop closes only if each write is a no-op the second
    /// time round. All three of them are idempotent — the window width, 90% of its height and the
    /// bottom inset are the same numbers on every pass — so the original converged after one extra
    /// layout rather than spinning. It was still one extra full measure of the sheet per size change,
    /// and an idempotent write that happens to stop being idempotent (a different inset on a keyboard
    /// animation, say) turns that into a genuine feedback loop. The comparisons cost three doubles and
    /// make the question disappear rather than merely currently being benign.
    /// </remarks>
    private void SizeToWindow()
    {
        try
        {
            var window = Application.Current?.Windows.FirstOrDefault();
            var width = window?.Width ?? 0;
            if (width <= 0) width = this.Width;
            if (width <= 0) width = DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
            if (width > 0 && Math.Abs(WidthRequest - width) > 0.5) WidthRequest = width;

            var height = window?.Height ?? 0;
            if (height > 0 && SheetScroll is not null)
            {
                var cap = height * 0.9;
                if (Math.Abs(SheetScroll.MaximumHeightRequest - cap) > 0.5) SheetScroll.MaximumHeightRequest = cap;
            }

            if (SheetContent is not null)
            {
                var padding = new Thickness(16, 8, 16, 16 + GetBottomInset());
                if (SheetContent.Padding != padding) SheetContent.Padding = padding;
            }
        }
        catch (Exception exception)
        {
            AppLog.Exception("TimePickerPopup.SizeToWindow", exception);
        }
    }

    /// <summary>
    /// The height of the system navigation bar in device-independent pixels, or 0 when there is none.
    /// Android-only; the other platforms either have no bottom bar or inset their content themselves.
    /// </summary>
    private static double GetBottomInset()
    {
        try
        {
#if ANDROID
            var resources = Platform.CurrentActivity?.Resources;
            if (resources is not null)
            {
                var resourceId = resources.GetIdentifier("navigation_bar_height", "dimen", "android");
                if (resourceId > 0)
                {
                    var bottomPx = resources.GetDimensionPixelSize(resourceId);
                    var metrics = resources.DisplayMetrics;
                    if (bottomPx > 0 && metrics is not null) return bottomPx / metrics.Density;
                }
            }
#endif
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
