using System.Globalization;

namespace CafePos.Core.Common;

/// <summary>Presentation-neutral helpers shared by ViewModels (and covered by unit tests).</summary>
public static class TextFormat
{
    /// <summary>Formats a duration in minutes as "1 ч 12 мин" / "12 мин" / "нет данных".</summary>
    public static string Duration(double minutes)
    {
        if (minutes <= 0) return "нет данных";
        var duration = TimeSpan.FromMinutes(minutes);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} ч {duration.Minutes} мин"
            : $"{duration.Minutes} мин";
    }

    /// <summary>Formats a money value as "220,00 ₽".</summary>
    public static string Money(decimal value) => $"{Common.Money.Round(value).ToString("F2", CultureInfo.CurrentCulture)} ₽";

    /// <summary>
    /// Formats a stock/recipe quantity with its unit as "250 г" / "0,5 л". Trailing zeros are
    /// dropped, so an integral amount reads "250 г" rather than "250,000 г".
    /// </summary>
    public static string Quantity(decimal value, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? value.ToString("0.###", CultureInfo.CurrentCulture)
            : $"{value.ToString("0.###", CultureInfo.CurrentCulture)} {unit.Trim()}";

    /// <summary>Formats a unit cost with its unit as "12 ₽/г".</summary>
    public static string CostPerUnit(decimal value, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? Money(value)
            : $"{value.ToString("0.##", CultureInfo.CurrentCulture)} ₽/{unit.Trim()}";

    /// <summary>Parses user input in both the current and the invariant culture.</summary>
    public static bool TryParseDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
        || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>Splits a comma separated list, removes blanks and duplicates.</summary>
    public static List<string> ParseList(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
