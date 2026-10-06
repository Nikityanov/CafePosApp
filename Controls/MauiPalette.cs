using CafePos.Presentation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Controls;

/// <summary>
/// <see cref="IPalette"/> over MAUI's application resources.
/// </summary>
/// <remarks>
/// <para>
/// The one place the palette is reached. Everything above it — which key a status maps to, which tone
/// means which surface — is decided in the ViewModels; this only answers "what colour is that key in
/// the theme running right now", and it can answer that because it lives inside the MAUI project and
/// so may touch <c>Application.Current</c>. That single call is the entire reason
/// <see cref="IPalette"/> has to exist.
/// </para>
/// <para>
/// It reads <c>Application.Current.Resources</c> on every call rather than caching, which is the same
/// honest limit the original helper had and the reason it is still the right shape: the ViewModels are
/// registered transient and subscribing each to <c>RequestedThemeChanged</c> would leak a handler per
/// instance. A screen rebuilt after a theme change picks up the new palette; a colour already handed
/// to a binding does not. That is a stated limitation, not a bug deferred.
/// </para>
/// </remarks>
internal sealed class MauiPalette : IPalette
{
    public Color Resolve(string lightKey, string darkKey)
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return TryGetColor(dark ? darkKey : lightKey)   // the requested theme
            ?? TryGetColor(lightKey)                    // dark token missing
            ?? TryGetColor(darkKey)                     // light token missing
            ?? Colors.Black;                             // a key was renamed outright
    }

    /// <summary>
    /// Looks a named <c>&lt;Color x:Key="…"&gt;</c> up in the application resources, or null when
    /// the key is absent — so the caller chooses its own fallback rather than throwing on a rename.
    /// </summary>
    public static Color? TryGetColor(string colorKey)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(colorKey, out var value) && value is Color color)
        {
            return color;
        }

        return null;
    }
}