namespace CafePosApp.Controls;

/// <summary>
/// Applies a keyed style from the application resources to a control built in C#.
/// </summary>
/// <remarks>
/// Controls assembled in code cannot use the <c>{StaticResource}</c> markup extension, so they look
/// the style up by key instead. Used by the catalogue action sheet so it does not have to repeat
/// the lookup.
/// </remarks>
internal static class ResourceStyles
{
    /// <summary>Applies the style if it exists. Returns false otherwise, leaving platform defaults.</summary>
    public static bool TryApply(StyleableElement target, string styleKey)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(styleKey, out var value) && value is Style style)
        {
            target.Style = style;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a named <see cref="Color"/> from the application resources.
    /// </summary>
    /// <remarks>
    /// The colour counterpart of <see cref="TryApply"/>: Colors.xaml declares its palette as
    /// <c>&lt;Color x:Key="Danger"&gt;…</c>, which is the same kind of app-level resource, so a
    /// control assembled in code needs the same by-key lookup. Returns null when the key is absent
    /// so the caller can decide what to do rather than throwing on a palette rename.
    /// </remarks>
    public static Color? TryGetColor(string colorKey)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(colorKey, out var value) && value is Color color)
            return color;

        return null;
    }
}
