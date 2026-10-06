using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace CafePosApp.Converters;

/// <summary>
/// Path data as a string → a <see cref="Geometry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CafePos.Presentation.ViewModels.OrderRowViewModel.PaymentGlyph"/> used to return a
/// <see cref="Geometry"/>, parsed once per process. That forced the ViewModels to reference the MAUI
/// framework for three path strings, so it now returns a string and this parses it.
/// </para>
/// <para>
/// <b>THE BUILD DOES NOT CATCH THE MISTAKE HERE, WHICH IS WHY THIS EXISTS.</b> Binding a <c>string</c>
/// to <c>shapes:Path.Data</c> compiles clean and fails at runtime on the row that draws, because
/// <c>Data</c> is typed <see cref="Geometry"/> and XAML's runtime setter does not convert. A green
/// build and 392 green tests both said nothing about it — the same shape of failure as the XAML
/// attribute-literal and nested-<c>x:Static</c> bugs this project has hit before.
/// </para>
/// <para>
/// It is deliberately the SAME converter XAML uses for a literal <c>Path="M …"</c>, so a glyph is
/// parsed once in one place and the mark means the same thing however it arrives.
/// </para>
/// </remarks>
public sealed class PathDataToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            // An EMPTY GEOMETRY, not null and not a sentinel. Two reasons, both learned the hard way:
            // Path.Data rejects null at bind time, and there is no BindingOperations.DoNothing in this
            // MAUI version — the type does not exist, so the "correct" sentinel cannot be named.
            //
            // An empty path is also the honest answer: a row with no glyph should draw no glyph, and it
            // costs nothing on a row that never hits this branch.
            return new PathGeometry();
        }

        // Geometry has no Parse method, which is why this is a converter and not a constructor arg.
        // A malformed path therefore falls back to an empty one rather than throwing inside a binding
        // — the mark is a fourth signal beside colour and wording, so losing it must not lose the row.
        return new PathGeometryConverter().ConvertFromString(path) as Geometry ?? new PathGeometry();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(PathDataToGeometryConverter)} works one way only.");
}