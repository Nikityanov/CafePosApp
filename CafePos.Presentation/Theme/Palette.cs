using Microsoft.Maui.Graphics;

namespace CafePos.Presentation;

/// <summary>
/// The app's palette, read by key, in the theme currently in effect.
/// </summary>
/// <remarks>
/// <para>
/// This interface exists because of one dependency, and it is worth being exact about which one.
/// A <see cref="Color"/> is a value type in a graphics assembly — it compiles and runs on plain
/// net10.0 with no MAUI workload, and that is why <see cref="Color"/> survives the move to this
/// project untouched. What does NOT survive is <c>Application.Current.Resources</c>, which is how the
/// palette is actually reached: the ViewModels resolved 27 colours through it and it is MAUI's
/// <c>Application</c>, a UI type.
/// </para>
/// <para>
/// So the seam is not "colour" but "the lookup". Everything above it — which key a severity maps to,
/// which tone means which surface — stays in the ViewModels, and the one thing that genuinely needs
/// an application is a one-method interface the MAUI project implements.
/// </para>
/// <para>
/// <b>WHY BY KEY AND NOT BY VALUE.</b> The alternative was a second copy of the palette in C#, and a
/// palette in two places is a palette that drifts: the XAML retunes <c>Danger</c> and the ViewModel
/// keeps last month's red, which is exactly the bug this lookup was written to fix (see
/// <c>Converters/NoticeSeverityConverters.cs</c> for the contrast failures that motivated it).
/// Reading by key means one definition.
/// </para>
/// </remarks>
public interface IPalette
{
    /// <summary>
    /// The colour for <paramref name="lightKey"/> in the light theme and <paramref name="darkKey"/>
    /// in the dark one.
    /// </summary>
    /// <remarks>
    /// Falls back rather than throwing, in this order: the token for the requested theme, the token
    /// for the other theme, and finally a neutral black. A palette rename degrades to a wrong-but-
    /// visible colour; it does not take a screen down at the moment an operator opens it.
    /// </remarks>
    Color Resolve(string lightKey, string darkKey);
}