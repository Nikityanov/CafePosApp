using System.Globalization;
using CafePosApp.Controls;

namespace CafePosApp.Converters;

/// <summary>
/// Resolves a palette key pair against the theme the app is currently running in.
/// </summary>
/// <remarks>
/// Colours produced in C# never participate in theming on their own. A
/// <see cref="Microsoft.Maui.Graphics.Color"/> returned by a ViewModel and bound straight into
/// <c>TextColor</c> or <c>Border.Stroke</c> is a finished value: <c>AppThemeBinding</c> only
/// governs values that come from XAML, so a <c>Microsoft.Maui.Graphics.Colors.*</c> literal in a
/// ViewModel is permanently light-themed. That is how the shift history came to render
/// <c>SteelBlue #4682B4</c> for a closed order on a <c>SurfaceDark #1E1E1E</c> card — 4.06:1,
/// and only 3.35:1 on a <c>SurfaceVariantDark #2D2D2D</c> one, which is below 4.5:1 for the 13pt
/// label it was drawn on.
/// <para>
/// The fix is not a second copy of the palette in C#. It is to ask
/// <see cref="ResourceStyles"/> for the same <c>&lt;Color x:Key="…"&gt;</c> the XAML styles ask
/// for, and pick between the light and dark key by
/// <see cref="Application.RequestedTheme"/> — the same check
/// <c>Views/CatalogActionSheetPopup.xaml.cs</c> already performs for its divider. A dark-mode
/// token is added to Colors.xaml beside its light pair for exactly this: see
/// <c>DangerDark</c>/<c>WarningDark</c>/<c>SuccessDark</c>/<c>InfoDark</c>.
/// </para>
/// <para>
/// Two honest limits, both inherent to reading a value rather than binding one. The colour is
/// resolved each time the property is read, so a page rebuild or a re-bound row picks the new
/// theme up — but nothing pushes a change to a colour that was already handed to a binding, since
/// the ViewModels are registered transient and subscribing them to
/// <c>Application.RequestedThemeChanged</c> would leak a handler per instance. And the palette key
/// is looked up by name, so renaming a key degrades to the fallback below rather than throwing.
/// </para>
/// <para>
/// This belongs next to <see cref="ResourceStyles.TryGetColor"/> in <c>Controls/ResourceStyles.cs</c>;
/// it lives in this file only because that is where the first caller outside a XAML page was, and
/// adding a file for it was not on the table.
/// </para>
/// </remarks>
internal static class ThemeColors
{
    /// <summary>
    /// The colour for <paramref name="lightKey"/> in the light theme and
    /// <paramref name="darkKey"/> in the dark theme.
    /// </summary>
    public static Color Resolve(string lightKey, string darkKey)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return ResourceStyles.TryGetColor(dark ? darkKey : lightKey)   // the requested theme
            ?? ResourceStyles.TryGetColor(lightKey)                       // dark token missing
            ?? ResourceStyles.TryGetColor(darkKey)                        // light token missing
            ?? Colors.Black;                                              // a key was renamed outright
    }
}

/// <summary>
/// Colour for a <see cref="CafePos.Presentation.ViewModels.CatalogManagementViewModel.NoticeLevel"/>.
/// </summary>
/// <remarks>
/// The catalogue notice label used to hard-wire <c>Danger</c>, so "Скопировано: …" rendered in
/// red. The colour now comes from the severity — but colour is never the only cue: the page
/// pairs it with a different leading icon and the message wording already differs.
/// <para>
/// The two hex literals this used to carry duplicated Colors.xaml, so the notice stayed the light
/// palette's green and red in dark mode (Success on SurfaceDark is 3.25:1). Both now come from
/// <see cref="ThemeColors"/>, which reads the palette, so the notice follows the theme.
/// </para>
/// </remarks>
public sealed class NoticeSeverityToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CafePos.Presentation.ViewModels.CatalogManagementViewModel.NoticeLevel.Success => ThemeColors.Resolve("Success", "SuccessDark"),
            _ => ThemeColors.Resolve("Danger", "DangerDark")
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException($"{nameof(NoticeSeverityToColorConverter)} works one way only.");
}

/// <summary>
/// Leading icon for a <see cref="CafePos.Presentation.ViewModels.CatalogManagementViewModel.NoticeLevel"/>:
/// a check mark for a success, an exclamation for an error. The pair is what makes the notice
/// readable without relying on colour.
/// </summary>
public sealed class NoticeSeverityToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            CafePos.Presentation.ViewModels.CatalogManagementViewModel.NoticeLevel.Success => SuccessGlyph,
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
