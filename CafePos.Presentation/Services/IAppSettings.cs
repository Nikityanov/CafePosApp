using CafePos.Core.Common;

namespace CafePos.Presentation.Services;

/// <summary>
/// The operator's device-level settings, as the ViewModels read them.
/// </summary>
/// <remarks>
/// <para>
/// The port exists because the concrete <c>AppSettings</c> is two MAUI calls wearing a plain
/// property bag: <c>Preferences.Get/Set</c> for storage and <c>Application.Current.UserAppTheme</c>
/// for applying the theme. Both are platform, so the type could not move into this project as it
/// stood — and the five ViewModels that read <c>OrderPrefix</c> to build an order title could not
/// move without it.
/// </para>
/// <para>
/// <b>Theme IS NOT A <c>AppTheme</c> HERE.</b> That enum is MAUI's, and taking it across this
/// boundary would put the framework back in the signature for no gain — the one thing the callers do
/// with the theme is round-trip it through three Russian titles. So the port speaks
/// <see cref="ThemePreference"/>, a three-value enum of this layer's own, and the MAUI side maps it.
/// </para>
/// <para>
/// <see cref="ApplyTheme"/> stays on the port even though it is the most platform-shaped member here,
/// because <c>SettingsViewModel</c> needs it and inventing a second <c>ITimeHost</c>-style interface
/// for one call would be the same mistake in a smaller size. A port is allowed to be one method wide
/// when that method is genuinely not portable; the alternative is a method whose only job is to call
/// another method.
/// </para>
/// </remarks>
public interface IAppSettings
{
    string CafeName { get; set; }

    /// <summary>How often the orders board refreshes itself, in seconds. Clamped to 5–300.</summary>
    int AutoRefreshSeconds { get; set; }

    /// <summary>
    /// What an order is called in front of its number — «Заказ #12». Persisted by code, not by this
    /// translated title.
    /// </summary>
    string OrderPrefix { get; set; }

    /// <summary>
    /// The currency shown across the app. Backed by <c>CurrencySelection</c> upstream, which owns the
    /// single preferences key — so this deliberately does not persist a second copy.
    /// </summary>
    Currency CurrencyCode { get; set; }

    ThemePreference Theme { get; set; }

    /// <summary>
    /// Pushes <see cref="Theme"/> onto the running application. Called immediately after the value is
    /// set, because writing the preference does not change what is on screen.
    /// </summary>
    void ApplyTheme();
}

/// <summary>
/// The three theme choices the settings screen offers.
/// </summary>
/// <remarks>
/// Mirrors MAUI's <c>AppTheme</c> one-for-one, on purpose: there is no fourth state, and inventing a
/// mapping table would be a place for the two to drift. <c>Unspecified</c> means "follow the system",
/// which is why it is not called <c>System</c> — the value the operator picks is a Russian title and
/// the stored value is this.
/// </remarks>
public enum ThemePreference
{
    /// <summary>Follow the system setting.</summary>
    System,

    Light,

    Dark,
}