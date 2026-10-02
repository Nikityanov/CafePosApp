using System.Globalization;
using CafePos.Core.Common;

namespace CafePosApp.Tests;

public class TextFormatTests
{
    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("0", 0.0)]
    [InlineData(" 7 ", 7.0)]
    public void TryParseDecimal_accepts_invariant_format(string input, double expected)
    {
        Assert.True(TextFormat.TryParseDecimal(input, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void TryParseDecimal_rejects_garbage(string? input) =>
        Assert.False(TextFormat.TryParseDecimal(input, out _));

    [Fact]
    public void TryParseDecimal_accepts_the_current_culture_too()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.True(TextFormat.TryParseDecimal("12,5", out var value));
            Assert.Equal(12.5m, value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(72, "1 ч 12 мин")]
    [InlineData(12, "12 мин")]
    [InlineData(0, "нет данных")]
    [InlineData(-5, "нет данных")]
    public void Duration_formats_minutes(double minutes, string expected) =>
        Assert.Equal(expected, TextFormat.Duration(minutes));

    [Fact]
    public void Money_formats_with_two_decimals()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal("220,00 ₽", TextFormat.Money(220m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "many")]
    [InlineData(1, "one")]
    [InlineData(2, "few")]
    [InlineData(4, "few")]
    [InlineData(5, "many")]
    [InlineData(11, "many")]
    [InlineData(12, "many")]
    [InlineData(14, "many")]
    [InlineData(21, "one")]
    [InlineData(22, "few")]
    [InlineData(25, "many")]
    [InlineData(101, "one")]
    [InlineData(111, "many")]
    [InlineData(1000, "many")]
    public void Plural_picks_the_russian_form_for_each_count(int count, string expected)
    {
        // The teen rule is the point of the 11–14 band: their last digit is 1–4, so checking the
        // units digit first would give «11 позиция» and «14 позиции» instead of «many».
        var form = TextFormat.Plural(count, "one", "few", "many");
        Assert.Equal(expected, form);
    }

    [Fact]
    public void ParseList_trims_splits_and_deduplicates()
    {
        Assert.Equal(["молоко", "сахар"], TextFormat.ParseList(" молоко, сахар,,МОЛОКО "));
        Assert.Empty(TextFormat.ParseList(null));
        Assert.Empty(TextFormat.ParseList("   "));
    }

    [Theory]
    [InlineData(250, "г", "250 г")]
    [InlineData(0.5, "л", "0,5 л")]
    [InlineData(0.02, "кг", "0,02 кг")]
    [InlineData(1, "шт", "1 шт")]
    public void Quantity_drops_trailing_zeros_and_appends_the_unit(double value, string unit, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal(expected, TextFormat.Quantity((decimal)value, unit));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Quantity_omits_a_missing_unit(string? unit)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal("250", TextFormat.Quantity(250m, unit));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(12.5, "г", "12,5 ₽/г")]
    [InlineData(12, "г", "12 ₽/г")]
    [InlineData(12, null, "12,00 ₽")]
    public void CostPerUnit_renders_a_rate_or_plain_money(double value, string? unit, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal(expected, TextFormat.CostPerUnit((decimal)value, unit));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ── Currency ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("BYN", "220,00 ₿")]   // the one this app needs: 2 digits, its own sign
    [InlineData("RUB", "220,00 ₽")]
    [InlineData("USD", "220,00 $")]
    [InlineData("EUR", "220,00 €")]
    [InlineData("JPY", "220 ¥")]      // no minor unit, so no decimals and no trailing zeros
    public void Money_uses_the_given_currencys_sign_and_digit_count(string code, string expected)
    {
        using var _ = new RussianCulture();

        Assert.Equal(expected, TextFormat.Money(220m, Currencies.FromCode(code)));
    }

    [Fact]
    public void Money_falls_back_to_the_ambient_default_currency()
    {
        using var _ = new RussianCulture();

        // The ambient default is what every call site that does not name a currency reads, and the
        // app publishes the operator's setting into it. Resetting in the finally is what keeps this
        // test from leaking a Belarusian ruble into the 129 that run after it.
        var previous = Currencies.Default;
        try
        {
            Currencies.Default = Currencies.FromCode("BYN");
            Assert.Equal("220,00 ₿", TextFormat.Money(220m));
        }
        finally
        {
            Currencies.Default = previous;
        }
    }

    [Fact]
    public void The_two_currencies_that_share_the_yen_sign_differ_only_in_decimals()
    {
        using var _ = new RussianCulture();

        // CNY and JPY are both ¥. If the digit count did not come from the currency, these two
        // would render identically and the setting would be indistinguishable for them.
        //
        // No group separator in the expected strings: "F2" does not group digits, so ru-RU prints
        // 1234,50 rather than 1 234,50. Asserting the grouped form here would have been asserting
        // a formatting choice this app never made.
        var cny = TextFormat.Money(1234.5m, Currencies.FromCode("CNY"));
        var jpy = TextFormat.Money(1234.5m, Currencies.FromCode("JPY"));
        Assert.Equal("1234,50 ¥", cny);
        Assert.Equal("1235 ¥", jpy);
        Assert.NotEqual(cny, jpy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XXX")]        // a real ISO code for a currency that is not a preset
    [InlineData("not a code")]
    public void An_unusable_currency_code_degrades_to_the_ruble_rather_than_throwing(string? code)
    {
        // The value comes from device preferences, which can be hand-edited, restored from an older
        // build, or truncated. None of those may stop the till from opening.
        Assert.Equal(Currencies.Ruble, Currencies.FromCode(code));
    }

    [Fact]
    public void Currency_codes_resolve_case_insensitively_and_ignore_surrounding_space()
    {
        Assert.Equal(Currencies.BelarusianRuble, Currencies.FromCode(" byn "));
        Assert.Equal(Currencies.BelarusianRuble, Currencies.FromCode("BYN"));
    }

    [Fact]
    public void Every_preset_declares_a_sign_a_two_or_zero_digit_count_and_a_minor_unit_name()
    {
        // The presets are the whole contract of the setting. A currency with, say, 1 or 3 digits
        // cannot be represented on the hundredths grid the database stores amounts on, so the
        // catalog is asserted to contain only the two representable shapes rather than being
        // trusted to stay that way.
        Assert.All(Currencies.All, currency =>
        {
            Assert.False(string.IsNullOrWhiteSpace(currency.Symbol), $"{currency.Code} has no sign");
            Assert.False(string.IsNullOrWhiteSpace(currency.MinorUnitName), $"{currency.Code} has no minor unit name");
            Assert.Contains(currency.MinorUnitDigits, (int[])[0, 2]);
        });
    }

    [Fact]
    public void Preset_codes_are_unique_so_a_saved_setting_cannot_resolve_two_ways()
    {
        var codes = Currencies.All.Select(currency => currency.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_belarusian_ruble_is_among_the_presets_with_its_own_sign_and_two_digits()
    {
        // Stated as its own test because it is the reason this feature exists: an operator in
        // Belarus must be able to pick ₿, and one unit is 100 kapeyek.
        var byn = Currencies.FromCode("BYN");
        Assert.Equal("₿", byn.Symbol);
        Assert.Equal(2, byn.MinorUnitDigits);
        Assert.Equal("Белорусский рубль", byn.Title);
    }

    /// <summary>Sets ru-RU for the duration of a test and restores the previous culture after.</summary>
    /// <remarks>
    /// ru-RU, not the invariant culture: the expected strings here all use a comma as the decimal
    /// separator and a non-breaking space as the group separator, which is what the till actually
    /// prints. Under the invariant culture every one of them would be "220.00" and these tests
    /// would pass while the app rendered dots.
    /// </remarks>
    private sealed class RussianCulture : IDisposable
    {
        private readonly CultureInfo previous = CultureInfo.CurrentCulture;

        public RussianCulture() => CultureInfo.CurrentCulture = new CultureInfo("ru-RU");

        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
