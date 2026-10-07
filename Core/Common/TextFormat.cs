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

    /// <remarks>`docs/decisions/shared.md`</remarks>

    public static string Money(decimal value) => Money(value, Currencies.Default);

    /// <summary>Formats a money value in the given currency, e.g. "220,00 ₿" or "220 ¥".</summary>
    public static string Money(decimal value, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return $"{Common.Money.Round(value).ToString(currency.NumericFormat, CultureInfo.CurrentCulture)} {currency.Symbol}";
    }

    /// <summary>Formats a stock/recipe quantity with its unit as "250 г" / "0,5 л". Trailing zeros are dropped, so an integral amount reads "250 г" rather than "250,000 г".</summary>

    public static string Quantity(decimal value, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? value.ToString("0.###", CultureInfo.CurrentCulture)
            : $"{value.ToString("0.###", CultureInfo.CurrentCulture)} {unit.Trim()}";

    /// <summary>Formats a unit cost with its unit as "12 ₽/г".</summary>
    public static string CostPerUnit(decimal value, string? unit) => CostPerUnit(value, unit, Currencies.Default);

    /// <summary>Formats a unit cost in the given currency as "12,50 ₿/г".</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string CostPerUnit(decimal value, string? unit, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return string.IsNullOrWhiteSpace(unit)
            ? Money(value, currency)
            : $"{value.ToString("0.##", CultureInfo.CurrentCulture)} {currency.Symbol}/{unit.Trim()}";
    }

    /// <summary>Parses user input in both the current and the invariant culture.</summary>
    public static bool TryParseDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
        || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>Selects the Russian plural form for a count: one / few / many.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string Plural(int count, string one, string few, string many)
    {
        if ((uint)(count % 100) is >= 11 and <= 14) return many;
        return (uint)(count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    /// <summary>Splits a comma separated list, removes blanks and duplicates.</summary>
    public static List<string> ParseList(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
