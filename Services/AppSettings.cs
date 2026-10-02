using CafePos.Core.Common;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace CafePosApp.Services;

public sealed class AppSettings
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
    public AppTheme Theme
    {
        get => Preferences.Get(ThemeKey, "System") switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
        set => Preferences.Set(ThemeKey, value switch { AppTheme.Light => "Light", AppTheme.Dark => "Dark", _ => "System" });
    }

    public void ApplyTheme()
    {
        if (Application.Current is not null) Application.Current.UserAppTheme = Theme;
    }
}
