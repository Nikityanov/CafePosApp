namespace CafePos.Core.Common;

/// <summary>A currency the till can display: its ISO code, the sign to print, and how many minor-unit digits it has.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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

    /// <summary>The currency used by every formatting call that does not name one explicitly. Set once at startup from the operator's setting.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public static Currency Default { get; set; } = Ruble;

    /// <summary>Belarusian ruble.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public static readonly Currency BelarusianRuble = new("BYN", "Белорусский рубль", "₿", 2, "капеек");

    /// <summary>Every preset, ordered so the two rubles sit at the top and the rest follow by region relevance to a café POS.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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

    /// <summary>Resolves a persisted code back to a currency.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
