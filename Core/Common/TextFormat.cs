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
    /// <remarks>
    /// The currency overload takes the currency explicitly; this one resolves
    /// <see cref="Currencies.Default"/>, which the app sets from the operator's setting at
    /// startup. "220,00 ₽" therefore becomes "220,00 ₿" for the Belarusian ruble and "220 ¥"
    /// for the yen with no change at any of the call sites.
    /// </remarks>
    public static string Money(decimal value) => Money(value, Currencies.Default);

    /// <summary>Formats a money value in the given currency, e.g. "220,00 ₿" or "220 ¥".</summary>
    public static string Money(decimal value, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return $"{Common.Money.Round(value).ToString(currency.NumericFormat, CultureInfo.CurrentCulture)} {currency.Symbol}";
    }

    /// <summary>
    /// Formats a stock/recipe quantity with its unit as "250 г" / "0,5 л". Trailing zeros are
    /// dropped, so an integral amount reads "250 г" rather than "250,000 г".
    /// </summary>
    public static string Quantity(decimal value, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? value.ToString("0.###", CultureInfo.CurrentCulture)
            : $"{value.ToString("0.###", CultureInfo.CurrentCulture)} {unit.Trim()}";

    /// <summary>Formats a unit cost with its unit as "12 ₽/г".</summary>
    public static string CostPerUnit(decimal value, string? unit) => CostPerUnit(value, unit, Currencies.Default);

    /// <summary>
    /// Formats a unit cost in the given currency as "12,50 ₿/г". Note the trailing zeros are
    /// dropped for the fraction ("0.##") while <see cref="Money"/> pads to the currency's digit
    /// count: a per-gram cost is a derived figure where "12,5 ₿/г" reads better than "12,50", and
    /// a unit price never needs to reconcile against a printed total the way a total does.
    /// </summary>
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

    /// <summary>Splits a comma separated list, removes blanks and duplicates.</summary>
    public static List<string> ParseList(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
