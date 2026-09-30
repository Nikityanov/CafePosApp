using System.Globalization;

namespace CafePosApp.Converters;

/// <summary>
/// Colour for a <see cref="ViewModels.CatalogManagementViewModel.NoticeLevel"/>.
/// </summary>
/// <remarks>
/// The catalogue notice label used to hard-wire <c>Danger</c>, so "Скопировано: …" rendered in
/// red. The colour now comes from the severity — but colour is never the only cue: the page
/// pairs it with a different leading icon and the message wording already differs.
/// </remarks>
public sealed class NoticeSeverityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ViewModels.CatalogManagementViewModel.NoticeLevel.Success => Color.FromArgb("#2E7D32"),  // Success
            _ => Color.FromArgb("#D32F2F")                                                          // Danger
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(NoticeSeverityToColorConverter)} works one way only.");
}

/// <summary>
/// Leading icon for a <see cref="ViewModels.CatalogManagementViewModel.NoticeLevel"/>:
/// a check mark for a success, an exclamation for an error. The pair is what makes the notice
/// readable without relying on colour.
/// </summary>
public sealed class NoticeSeverityToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ViewModels.CatalogManagementViewModel.NoticeLevel.Success => SuccessGlyph,
            _ => ErrorGlyph
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(NoticeSeverityToIconConverter)} works one way only.");

    // Glyphs, not vector geometry: the icon is a Label. shapes:Path.Data is a Geometry, and the
    // XAML loader only runs a *literal* attribute through the geometry TypeConverter — a Binding
    // hands the value straight to the property, so a converter returning a path string fails with
    // InvalidCastException. Both characters are in OpenSansRegular, the font every Label in this
    // app uses; U+2713 CHECK MARK is not (it would render as tofu), so the check is U+221A.
    private const string SuccessGlyph = "√";
    private const string ErrorGlyph = "!";
}
