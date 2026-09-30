using System.Globalization;

namespace CafePosApp.Converters;

/// <summary>
/// True when the value is present, false when it is null or an empty/whitespace string.
/// </summary>
/// <remarks>
/// Two jobs in the views, which is why it accepts both shapes:
/// <list type="bullet">
///   <item>optional cart suffixes ("[Large]", "(Oat milk)") via <c>IsVisible</c> — those lines
///   were bound unconditionally, so an order with neither a variant nor a modifier still
///   reserved two rows;</item>
///   <item>optional navigation properties, e.g. a product's modifier group badge. Binding an
///   arbitrary object straight to a <c>bool</c> relies on the runtime type converter, which is
///   not guaranteed to agree with "not null".</item>
/// </list>
/// </remarks>
public sealed class IsPresentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => false,
        string text => !string.IsNullOrWhiteSpace(text),
        _ => true
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(IsPresentConverter)} works one way only.");
}
