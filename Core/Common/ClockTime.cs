using System.Globalization;

namespace CafePos.Core.Common;

/// <summary>Formats a clock time of day — «14:20» — from the two things this app holds one as: a inside the "when" sheet, and a on the order.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public static class ClockTime
{
    /// <remarks>`docs/decisions/shared.md`</remarks>

    public static string Format(TimeSpan timeOfDay) =>
        timeOfDay.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <remarks>`docs/decisions/shared.md`</remarks>

    public static string Format(DateTimeOffset moment) => Format(moment, TimeZoneInfo.Local);

    /// <summary>«14:20» for a timestamp read in a named zone.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string Format(DateTimeOffset moment, TimeZoneInfo? zone) =>
        Format(TimeZoneInfo.ConvertTime(moment, zone ?? TimeZoneInfo.Local).TimeOfDay);

    /// <summary>The next moment on the `gridMinutes` grid at or after `timeOfDay`, wrapping at midnight.</summary>

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

    /// <summary>«Сейчас плюс », on the grid — the seed of the "when" sheet and each of its three presets.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static TimeSpan NowPlus(int minutes, int gridMinutes) =>
        SnapUp(DateTime.Now.TimeOfDay + TimeSpan.FromMinutes(minutes), gridMinutes);
}
