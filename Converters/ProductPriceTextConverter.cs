using System.Globalization;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePosApp.Services;
using CafePos.Presentation.Services;

namespace CafePosApp.Converters;

/// <summary>
/// Price text for the product tiles. A product priced by its variants has no base price
/// (each variant carries its own), so the range covered by its variants is shown instead
/// of a meaningless "0 ₽".
/// </summary>
/// <remarks>
/// A single amount is rendered by <c>TextFormat.Money</c>, so a price reads the same
/// whether it came through a ViewModel or through this converter. The converter used to format
/// the decimal itself and skipped <see cref="Money.Round"/>, so the same price could be
/// displayed differently depending on the path it took to the screen.
/// </remarks>
public sealed class ProductPriceTextConverter : IValueConverter
{
    /// <param name="parameter">Optional .NET format string for a single price, "F2" by default.</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Product product) return string.Empty;

        var format = parameter as string;
        var currency = CurrencySelection.Current;
        if (!product.HasVariants) return WithCurrency(product.Price, format, culture, currency);

        if (product.Variants.Count == 0) return "цен нет";

        var low = product.Variants.Min(variant => variant.Price);
        var high = product.Variants.Max(variant => variant.Price);
        return low == high
            ? WithCurrency(low, format, culture, currency)
            // Only the upper bound carries the sign, so "100 – 150 ₽" instead of "100 ₽ – 150 ₽".
            : $"{Amount(low, format ?? DefaultFormat, culture)} – {WithCurrency(high, format, culture, currency)}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(ProductPriceTextConverter)} works one way only.");

    /// <summary>Format used for the lower bound of a range, which never carries a currency sign.</summary>
    private const string DefaultFormat = "F2";

    /// <summary>
    /// One amount plus its currency. Without an explicit format the shared formatter is the
    /// single source of truth; a format keeps the catalogue list's whole-ruble "F0" tiles working.
    /// </summary>
    /// <remarks>
    /// The symbol comes from the caller's <paramref name="currency"/> rather than being written
    /// here, and the "F0" tiles follow that currency's own whole-unit convention instead of
    /// hard-coding "F0": for the yen, where the minor unit does not exist, F0 is the only sensible
    /// tile, and for every 2-digit currency it is the same tile it always was.
    /// </remarks>
    private static string WithCurrency(decimal value, string? format, CultureInfo culture, Currency currency) =>
        format is null
            ? TextFormat.Money(value, currency)
            : $"{Amount(value, format, culture)} {currency.Symbol}";

    private static string Amount(decimal value, string format, CultureInfo culture) =>
        Money.Round(value).ToString(format, culture);
}
