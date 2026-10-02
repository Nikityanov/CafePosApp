namespace CafePos.Core.Common;

/// <summary>
/// A currency the till can display: its ISO code, the sign to print, and how many minor-unit
/// digits it has.
/// </summary>
/// <remarks>
/// WHY THE MINOR UNIT IS DISPLAY-ONLY
/// ==================================
/// Amounts are persisted as integer hundredths (see <see cref="Money"/>), because SQLite stores
/// decimals as TEXT and that breaks SUM/ORDER BY. So the storage grid is fixed at 1/100 of a unit
/// no matter which currency is selected, and <see cref="MinorUnitDigits"/> only decides how those
/// hundredths are PRINTED. Every preset with 2 digits maps one-to-one onto the stored value, so
/// switching currency never reinterprets a stored price — it only relabels it. That is deliberate:
/// a till must not silently reinterpret the money in the drawer when someone changes a setting.
///
/// A currency with 3 minor digits (KWD, BHD, OMR, JOD) cannot be represented exactly on a
/// hundredths grid, so those are deliberately absent from <see cref="Currencies"/> rather than
/// silently rounded. 0 digits (JPY) is exact the other way: it drops the sub-unit at display time,
/// which is what a Japanese price tag does.
/// </remarks>
/// <param name="Code">ISO 4217 code, also the persisted setting value.</param>
/// <param name="Title">Name for the settings list, in the app's language.</param>
/// <param name="Symbol">The sign printed after the amount.</param>
/// <param name="MinorUnitDigits">Digits after the decimal separator: 2 for kopecks/cents, 0 for yen.</param>
/// <param name="MinorUnitName">Name of the minor unit, shown in the settings list for clarity.</param>
public sealed record Currency(string Code, string Title, string Symbol, int MinorUnitDigits, string MinorUnitName)
{
    /// <summary>The format string for this currency's minor-unit digits, e.g. "F2" or "F0".</summary>
    public string NumericFormat => $"F{MinorUnitDigits}";
}

/// <summary>The currencies offered in Settings, each with its own sensible defaults.</summary>
public static class Currencies
{
    /// <summary>
    /// Russian ruble, and the app's default. Changing away from it changes presentation only.
    /// </summary>
    public static readonly Currency Ruble = new("RUB", "Российский рубль", "₽", 2, "копейки");

    /// <summary>
    /// The currency used by every formatting call that does not name one explicitly. Set once at
    /// startup from the operator's setting.
    /// </summary>
    /// <remarks>
    /// This is an ambient default on purpose, and it is the same pattern .NET itself uses for
    /// <c>TimeProvider.System</c>. The alternative — threading a currency through fifty-odd call
    /// sites — buys purity at the price of a domain that has to be told about a presentation
    /// setting on every call, and it still would not reach the two Core display properties
    /// (<see cref="Models.Ingredient.CostPerUnitText"/> and
    /// <see cref="Models.PriceHistoryEntry.ChangeText"/>) that are bound straight from XAML and
    /// have nowhere to receive a parameter.
    ///
    /// It is mutable, so it is a hazard for tests: a test that sets it must reset it, and the
    /// existing TextFormat tests are written against <see cref="Ruble"/>, which is the default, so
    /// they hold without touching this. Only the app sets it in anger
    /// (see Services.CurrencySelection).
    /// </remarks>
    public static Currency Default { get; set; } = Ruble;

    /// <summary>
    /// Belarusian ruble. Its sign is U+20BD, added in Unicode 14.0 (2021), so it is the one symbol
    /// here that a bundled Open Sans build may not carry — Android and iOS both fall back to a
    /// system font for a missing glyph, and this was checked on an API 36 emulator rather than
    /// assumed. One unit is 100 kapeyek, hence 2 digits, exactly like the Russian ruble.
    /// </summary>
    public static readonly Currency BelarusianRuble = new("BYN", "Белорусский рубль", "₿", 2, "капеек");

    /// <summary>
    /// Every preset, ordered so the two rubles sit at the top and the rest follow by region
    /// relevance to a café POS. JPY is included deliberately: it is the only 0-digit entry, so it
    /// is what proves the minor-unit setting actually does something rather than being decorative.
    /// </summary>
    public static readonly IReadOnlyList<Currency> All =
    [
        Ruble,
        BelarusianRuble,
        new("USD", "Доллар США", "$", 2, "цента"),
        new("EUR", "Евро", "€", 2, "цента"),
        new("KZT", "Казахстанский тенге", "₸", 2, "тиына"),
        new("UAH", "Украинская гривна", "₴", 2, "копейки"),
        new("PLN", "Польский злотый", "zł", 2, "гроши"),
        new("CZK", "Чешская крона", "Kč", 2, "гелши"),
        new("TRY", "Турецкая лира", "₺", 2, "куруша"),
        new("GBP", "Фунт стерлингов", "£", 2, "пенса"),
        new("CNY", "Китайский юань", "¥", 2, "фэня"),
        new("INR", "Индийская рупия", "₹", 2, "пайсы"),
        new("BRL", "Бразильский реал", "R$", 2, "сентаво"),
        new("JPY", "Японская иена", "¥", 0, "сэнов"),
    ];

    /// <summary>
    /// Resolves a persisted code back to a currency. An unknown or missing code falls back to the
    /// Russian ruble rather than throwing: the value comes from device preferences, and a
    /// preferences file that has been hand-edited, restored from an older build, or truncated
    /// must not be able to stop the till from opening.
    /// </summary>
    public static Currency FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Ruble;

        var normalized = code.Trim();
        foreach (var currency in All)
        {
            if (string.Equals(currency.Code, normalized, StringComparison.OrdinalIgnoreCase)) return currency;
        }

        return Ruble;
    }
}
