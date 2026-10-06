using CafePos.Core.Common;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// The MAUI implementation of the settings port: <c>Preferences</c> for storage,
/// <c>Application.Current</c> for applying the theme.
/// </summary>
/// <remarks>
/// The port lives in CafePos.Presentation because five ViewModels read the order prefix to title an
/// order, and they could not move out of the MAUI project while this type sat here. Nothing about the
/// STORAGE changed in that move — the same four keys, the same defaults, the same clamping — so an
/// existing install reads back exactly what it wrote before.
/// <para>
/// The one type that did change is <see cref="Theme"/>: it speaks <see cref="ThemePreference"/>
/// rather than MAUI's <c>AppTheme</c>, because an enum from the UI framework in a port's signature
/// would put the framework straight back across the boundary. The persisted value is the same string
/// in both, so the stored preference is untouched.
/// </para>
/// </remarks>
public sealed class AppSettings : IAppSettings
{
    private const string CafeNameKey = "cafe_name";
    private const string RefreshIntervalKey = "orders_refresh_interval";
    private const string ThemeKey = "app_theme";
    private const string OrderPrefixKey = "order_prefix";

    public string CafeName { get => Preferences.Get(CafeNameKey, "CafePOS"); set => Preferences.Set(CafeNameKey, value.Trim()); }
    public int AutoRefreshSeconds
    {
        get => Math.Clamp(Preferences.Get(RefreshIntervalKey, 15), 5, 300);
        set => Preferences.Set(RefreshIntervalKey, Math.Clamp(value, 5, 300));
    }
    public string OrderPrefix { get => Preferences.Get(OrderPrefixKey, "Заказ"); set => Preferences.Set(OrderPrefixKey, value.Trim()); }

    /// <summary>
    /// The currency shown across the app. Reads and writes through <see cref="CurrencySelection"/>,
    /// which owns the single preferences key and cache — this property deliberately does NOT
    /// persist a second copy, so the setting has one home.
    /// </summary>
    public Currency CurrencyCode
    {
        get => CurrencySelection.Current;
        set => CurrencySelection.Set(value);
    }
    /// <summary>
    /// The operator's theme choice, stored as one of "System" / "Light" / "Dark" — the same three
    /// strings, and the same values, this used to store. Only the enum in the signature is new.
    /// </summary>
    public ThemePreference Theme
    {
        get => Preferences.Get(ThemeKey, "System") switch
        {
            "Light" => ThemePreference.Light,
            "Dark" => ThemePreference.Dark,
            _ => ThemePreference.System
        };
        set => Preferences.Set(ThemeKey, value switch
        {
            ThemePreference.Light => "Light",
            ThemePreference.Dark => "Dark",
            _ => "System"
        });
    }

    /// <summary>
    /// Pushes <see cref="Theme"/> onto the running application.
    /// </summary>
    /// <remarks>
    /// Separate from the setter because writing the preference does not change what is on screen, and
    /// a silent no-op here would leave the operator looking at the old theme having been told it saved.
    /// <para>
    /// The null check is not defensiveness about a missing Application — it is that this also runs
    /// from tests and from the design-time path, where <c>Application.Current</c> legitimately does
    /// not exist and there is no screen to repaint anyway.
    /// </para>
    /// </remarks>
    public void ApplyTheme()
    {
        if (Application.Current is not null)
        {
            Application.Current.UserAppTheme = Theme switch
            {
                ThemePreference.Light => AppTheme.Light,
                ThemePreference.Dark => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };
        }
    }
}

