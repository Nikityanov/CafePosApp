namespace CafePos.Core.Common;

/// <summary>Time-window based availability of a menu item (e.g. breakfast 08:00-12:00).</summary>
public static class ProductAvailability
{
    public static bool IsInTimeWindow(int? fromHour, int? toHour, int hour)
    {
        if (fromHour is null && toHour is null) return true;

        var from = fromHour ?? 0;
        var to = toHour ?? 24;

        return from <= to
            ? hour >= from && hour < to
            : hour >= from || hour < to; // overnight window, e.g. 22 -> 6
    }
}
