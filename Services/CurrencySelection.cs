using CafePos.Core.Common;
using Microsoft.Maui.Storage;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// The currency the till is currently displaying, and the one place that persists it.
/// </summary>
/// <remarks>
/// WHY A STATIC, WHEN EVERYTHING ELSE HERE USES DI
/// ==============================================
/// Two kinds of code need the active currency and neither can take a constructor parameter:
///
///   1. The value converters declared in Resources/Styles/Styles.xaml. XAML news up its own
///      instances, so the container is not involved and there is nowhere to inject from.
///   2. Code-behind that builds labels in C# (the variant and draft pickers, the catalogue
///      action sheet), which would otherwise each need the setting threaded through a page.
///
/// So this is a static facade over <see cref="Preferences"/>, deliberately mirroring what
/// <see cref="AppSettings"/> already does for the theme. It holds ONE cache, so the two cannot
/// disagree; <see cref="AppSettings.CurrencyCode"/> reads and writes through here rather than
/// keeping a second copy of the key.
///
/// WHY NO CHANGE EVENT
/// ==================
/// Every ViewModel here is transient, and subscribing them to a static event would leak one
/// handler per instance with no page left to unsubscribe it. Instead each screen's existing
/// OnAppearing reload recomputes its formatted strings, and Shell fires OnAppearing on a tab
/// switch — so changing the currency in Settings and tapping back to «Меню» refreshes it, with
/// nothing to unsubscribe. Converters read <see cref="Current"/> at conversion time, so they are
/// always live with no notification at all.
/// </remarks>
public static class CurrencySelection
{
    private const string Key = "currency_code";

    private static Currency current = Currencies.Ruble;
    private static bool loaded;

    /// <summary>The currency in force. Loads from preferences on first use.</summary>
    public static Currency Current
    {
        get
        {
            if (!loaded) Reload();
            return current;
        }
    }

    /// <summary>
    /// Persists <paramref name="currency"/> and makes it current. Unknown codes are resolved by
    /// <see cref="Currencies.FromCode"/>, so a hand-edited preference cannot produce a broken state.
    /// </summary>
    public static void Set(Currency? currency)
    {
        var resolved = currency is null ? Currencies.Ruble : Currencies.FromCode(currency.Code);
        Preferences.Set(Key, resolved.Code);
        current = resolved;
        loaded = true;

        // The ambient default that Core's own formatting helpers read. Set here, in the one place
        // that owns the setting, rather than at each of its fifty-odd call sites.
        Currencies.Default = resolved;
    }

    /// <summary>
    /// Re-reads the persisted value. Called once at startup so the very first page painted already
    /// uses the stored currency instead of flashing the default and correcting itself.
    /// </summary>
    public static void Reload()
    {
        loaded = true;
        current = Currencies.FromCode(Preferences.Get(Key, Currencies.Ruble.Code));
        Currencies.Default = current;
    }
}

