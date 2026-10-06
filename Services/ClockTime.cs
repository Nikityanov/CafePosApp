using System.Globalization;

namespace CafePosApp.Services;

/// <summary>
/// Formats a clock time of day — «14:20» — from the two things this app holds one as: a
/// <see cref="TimeSpan"/> inside the "when" sheet, and a <see cref="DateTimeOffset"/> on the order.
/// </summary>
/// <remarks>
/// <b>WHY THIS EXISTS AT ALL: <c>TimeSpan.ToString("HH:mm")</c> THROWS.</b> A <see cref="TimeSpan"/>
/// has its own, much smaller set of custom format specifiers than a <see cref="DateTime"/>: they are
/// <c>d h m s f F</c>, all lowercase, and a colon has to be escaped. <c>"HH:mm"</c> therefore raises
/// <see cref="FormatException"/> — measured, not inferred:
/// <code>
/// "HH:mm"  =&gt; FormatException: Input string was not in a correct format.
/// "hh:mm"  =&gt; FormatException: Input string was not in a correct format.
/// "hh\:mm" =&gt; 14:20
/// </code>
/// On a <see cref="DateTimeOffset"/> the same pattern is fine, which is what makes the mistake so easy
/// to make: half this app's times are timestamps and half are durations, and the two accept different
/// format strings.
/// <para>
/// That one format string is not a typo — it was the whole of the crash reported as «выбор ко времени
/// вылетает». <c>TimePickerPopup.SetMode</c> formatted the chosen time with it to build the confirm
/// button's caption, on the UI thread, from an event handler, so the process died the instant the
/// operator chose anything other than «сейчас». Both routes reached it: pressing
/// «К выбранному времени», and moving the native dial (the dialog's own <c>onTimeSet</c> callback
/// assigns <c>TimePicker.Time</c>, which raises <c>PropertyChanged</c>, which called <c>SetMode</c>).
/// </para>
/// <para>
/// One method for both shapes rather than two call sites, so the escaping is decided once. It is here,
/// beside <see cref="TimePickResult"/>, because that record is where a chosen clock time first exists
/// as a value in this app.
/// </para>
/// </remarks>
public static class ClockTime
{
    /// <summary>«14:20» for any time of day, in the operator's own 24-hour convention.</summary>
    /// <remarks>
    /// Explicitly 24-hour, and not the current culture's short time pattern: a coffee shop's promise is
    /// quoted back to the customer in the same words all day long, and a locale that renders «2:20
    /// PM» beside a «22:20» on the order board produces exactly the ambiguity this sheet exists to
    /// remove. The escaped colon is the load-bearing part — see the remarks above.
    /// </remarks>
    public static string Format(TimeSpan timeOfDay) =>
        timeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>«14:20» for a timestamp, in the operator's own 24-hour convention.</summary>
    /// <remarks>
    /// The mirror of <see cref="Format(TimeSpan)"/>, and it needs no escaping — <see cref="DateTimeOffset"/>
    /// really does accept «HH:mm». Both overloads exist so the two call sites read alike and nobody has
    /// to remember which type they are holding.
    /// </remarks>
    public static string Format(DateTimeOffset moment) => Format(moment.ToLocalTime().TimeOfDay);

    /// <summary>
    /// The next moment on the <paramref name="gridMinutes"/> grid at or after <paramref name="timeOfDay"/>,
    /// wrapping at midnight.
    /// </summary>
    public static TimeSpan SnapUp(TimeSpan timeOfDay, int gridMinutes)
    {
        if (gridMinutes <= 1) return timeOfDay;

        var grid = gridMinutes * TimeSpan.TicksPerMinute;
        var remainder = timeOfDay.Ticks % grid;
        if (remainder == 0) return timeOfDay;

        var next = timeOfDay.Ticks - remainder + grid;
        // Wrapping is deliberate and correct: the grid is a 24-hour cycle, so 23:58 on a 5-minute grid
        // is 00:00, and an unwrapped 24:00 is a TimeSpan no TimePicker and no clock anywhere accepts.
        return TimeSpan.FromTicks(next % TimeSpan.TicksPerDay);
    }

    /// <summary>
    /// «Сейчас плюс <paramref name="minutes"/>», on the <paramref name="gridMinutes"/> grid — the seed of the
    /// "when" sheet and each of its three presets.
    /// </summary>
    /// <remarks>
    /// ONE method because the seed and the presets must agree, and a preset landing on a time the seed's
    /// grid could never reach would be a control that sets something the sheet cannot then show.
    /// </remarks>
    public static TimeSpan NowPlus(int minutes, int gridMinutes) =>
        SnapUp(DateTime.Now.TimeOfDay + TimeSpan.FromMinutes(minutes), gridMinutes);
}