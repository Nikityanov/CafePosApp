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
}
