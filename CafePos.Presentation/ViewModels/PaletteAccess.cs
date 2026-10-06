using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The palette, for the few types that need it before they have a constructor to inject into.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPalette"/> is the real seam, and every ViewModel that receives one in its constructor
/// uses it. This exists for exactly two cases where that is not possible, and both are here rather
/// than left as a duplicate copy of the palette:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>CategoryMenuItemViewModel</c> — built by static factories (<c>CreateAll</c>, <c>CreateCombos</c>)
/// and by a collection that fills in the rest, none of which has a place to pass a dependency. Its
/// colours are read once into a <c>static readonly</c> array at type initialisation, which is the real
/// constraint: an instance field would do the lookup per chip instead of once for all of them.
/// </description></item>
/// <item><description>
/// A ViewModel property that XAML binds to, where the getter runs with no context to resolve from.
/// </description></item>
/// </list>
/// <para>
/// <b>WHY A SERVICE LOCATOR AND NOT A STATIC PALETTE COPY.</b> The temptation was to move
/// <c>ThemeColors</c>'s body here and be done. That would put the key-to-colour mapping in this
/// project, where it cannot see the application resources, and the only way to resolve it would be a
/// lookup that always misses — the exact failure the palette-by-key design exists to prevent. A
/// locator that the MAUI project registers keeps ONE palette, read through the real resource
/// dictionary.
/// </para>
/// <para>
/// The cost is that this is global state and therefore untestable in isolation without the locator
/// being set. That is stated rather than hidden: a test asserts against <see cref="Set"/> and resets
/// it afterwards, and the alternative — a static copy of eight colours — would be testable and
/// wrong, because it would stop following the theme.
/// </para>
/// </remarks>
public static class PaletteAccess
{
    private static IPalette? current;

    /// <summary>
    /// The registered palette. Named <c>Current</c> rather than <c>Resolve</c> so it does not
    /// collide with the <c>Resolve(lightKey, darkKey)</c> below - a class cannot have both.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning null when unset, so a missing registration is a loud failure at
    /// the first colour read instead of a screen that quietly renders in the fallback grey. That
    /// loudness costs something, and the cost is named here rather than left for whoever hits it
    /// next: registering the palette is part of standing up a ViewModel, which is why
    /// <c>MenuHarness</c> does it once instead of leaving it to every test that constructs one.
    /// </remarks>
    public static IPalette Current =>
        current ?? throw new InvalidOperationException(
            $"{nameof(PaletteAccess)}.{nameof(Set)} was never called. The MAUI project must register " +
            "the palette before any ViewModel resolves a colour.");

    /// <summary>
    /// Registers the palette. Called once from the MAUI project's DI setup.
    /// </summary>
    public static void Set(IPalette palette) => current = palette;

    /// <summary>Resolves one key in both themes.</summary>
    public static Color Resolve(string lightKey, string darkKey) => Current.Resolve(lightKey, darkKey);

    /// <summary>Convenience for a single key that is deliberately the same tone in both themes.</summary>
    public static Color Same(string key) => Current.Resolve(key, key);
}
