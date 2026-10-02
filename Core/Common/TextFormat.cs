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

    /// <summary>
    /// Selects the Russian plural form for a count: one / few / many.
    /// </summary>
    /// <remarks>
    /// ONE implementation for the whole app, and it was private to
    /// <c>CatalogManagementViewModel</c> until a second screen needed it — which is exactly how
    /// "1 товаров" gets written twice in two files and fixed in only one. It belongs here beside the
    /// other Russian formatting helpers rather than in a ViewModel, because a word-choice rule is
    /// not a view concern and this file is what the test project already covers.
    /// <para>
    /// The teen rule comes first and is not a special case of the units rule: 11–14 take «many» even
    /// though their last digit is 1–4, so checking the units digit first gets 11 «позиция».
    /// </para>
    /// </remarks>
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
