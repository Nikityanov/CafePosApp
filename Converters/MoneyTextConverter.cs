using System.Globalization;
using CafePos.Core.Common;

namespace CafePosApp.Converters;

/// <summary>
/// Formats a bare <see cref="decimal"/> as money in the operator's selected currency.
/// </summary>
/// <remarks>
/// This exists for the places that bind a Core MODEL straight from XAML — the variant list on the
/// catalogue form binds <c>ProductVariant.Price</c>, which has no ViewModel to hang a formatted
/// property on. <c>StringFormat</c> cannot be used there either: it is fixed when the XAML is
/// parsed, so it can only ever emit the ruble sign with two decimals.
///
/// The optional parameter selects the digit count:
/// <list type="bullet">
/// <item>absent — the currency's own minor-unit digits ("220,00 ₿", "220 ¥")</item>
/// <item><c>F0</c> — whole units ("220 ₿"), for the compact tiles and list rows that have always
/// shown no minor unit</item>
/// </list>
/// Reading the currency per conversion is deliberate: it is a static lookup and the alternative —
/// a change event — would need every one of these bound models to subscribe, and a record has
/// nowhere to unsubscribe from.
/// </remarks>
public sealed class MoneyTextConverter : IValueConverter
{
    /// <param name="parameter">Optional .NET format string, e.g. "F0". Absent means the currency default.</param>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not decimal amount) return string.Empty;

        var currency = Currencies.Default;
        var format = parameter as string;
        if (string.IsNullOrWhiteSpace(format)) return TextFormat.Money(amount, currency);

        return $"{Money.Round(amount).ToString(format, culture)} {currency.Symbol}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(MoneyTextConverter)} works one way only.");
}
