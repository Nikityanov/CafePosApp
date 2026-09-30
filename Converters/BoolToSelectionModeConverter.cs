using System.Globalization;
using Microsoft.Maui.Controls;

namespace CafePosApp.Converters;

/// <summary>
/// Turns the catalogue's <c>IsSelectionMode</c> flag into a <see cref="SelectionMode"/>.
/// </summary>
/// <remarks>
/// The product list has two mutually exclusive tap behaviours: a tap opens the edit form, or a
/// tap ticks rows for a bulk price change. The list's <see cref="CollectionView.SelectionMode"/>
/// decides which one wins, so it has to be bound rather than fixed — a permanent
/// <c>Multiple</c> would have eaten the tap-to-edit gesture. Edit mode is <c>Single</c> (the
/// selection is read as a tap and immediately cleared), not <c>None</c>, because the page
/// needs the selection event to know a row was tapped.
/// <para>
/// The <c>using Microsoft.Maui.Controls;</c> is load-bearing for the doc comment. The converter's
/// own code resolves the enum from MAUI's implicit global using, but a <c>cref</c> is resolved
/// against this file's using directives, and without it the android target framework reported
/// CS1574 for <c>SelectionMode</c>.
/// </para>
/// </remarks>
public sealed class BoolToSelectionModeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? SelectionMode.Multiple : SelectionMode.Single;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SelectionMode.Multiple;
}
