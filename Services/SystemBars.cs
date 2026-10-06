// StatusBar and StatusBarStyle live in DIFFERENT namespaces in the toolkit: the enum is
// CommunityToolkit.Maui.Core (Primitives/StatusBarStyle.shared.cs) and the platform helper is
// CommunityToolkit.Maui.Core.Platform. Both usings are required.
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Core.Platform;

namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// Makes the OS-drawn status bar content — clock, battery, signal — legible against whatever
/// the app has actually painted behind it.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL
/// =====================
/// .NET MAUI decides the status bar icon colour from the MATERIAL THEME, not from the app's
/// own background. Since 10.0.100 <c>ConfigureTranslucentSystemBars</c> reads
/// <c>colorSurface</c> on Material 3 and <c>colorPrimary</c> on Material 2, and falls back to
/// the Android configuration's <c>UiMode</c> only when the colour cannot be resolved.
/// This app's <c>colorPrimary</c> is #143D59 — a dark navy, the brand colour, used for the
/// splash and nothing else. MAUI read that as "the surface behind the status bar is dark", so
/// it asked Android for WHITE icons, while the page behind the bar is
/// <c>BackgroundLight</c> #FAFAFA. Measured on an API 36 emulator: white clock, battery and
/// wifi glyphs on a near-white bar, effectively invisible.
///
/// WHY NOT AN ANDROID RESOURCE
/// ==========================
/// The obvious fix — split colors.xml into values/ and values-night/ — follows the SYSTEM
/// theme, and this app has an in-app theme picker (AppSettings.Theme -> UserAppTheme) that
/// deliberately overrides it. Choosing «Темная» while the phone is in light mode would then
/// paint light icons over a dark page. Worse, MAUI's own ConfigureTranslucentSystemBars
/// re-runs on window configuration and overrides whatever the theme resources said
/// (dotnet/maui#34462). So the resource route cannot express what this app needs.
///
/// The fix is therefore applied in code, from the APP's requested theme rather than the
/// system's, and re-applied at the points where MAUI resets it. It follows
/// <see cref="Application.RequestedTheme"/>, which already accounts for UserAppTheme, so
/// «Системная» keeps tracking the system and the two explicit choices override it.
///
/// WHERE IT IS APPLIED, AND WHY SO MANY PLACES
/// ===========================================
/// MAUI re-applies its own edge-to-edge configuration after lifecycle events, so a single
/// call at startup is not enough — the value is silently reverted. Apply() is therefore
/// called from three independent hooks (see App.CreateWindow and MainActivity.OnResume):
/// once the window handler exists, on every theme change, and on every Android resume,
/// which is the last event before the operator looks at the screen.
///
/// COLOUR IS NOT SET, ONLY THE ICON APPEARANCE
/// ===========================================
/// Setting a status bar colour would be wrong under edge-to-edge, where the bar is always
/// transparent and the page shows through: a painted fill would be a lie and would fight the
/// safe-area layout. Only the icon appearance is set, which is the one thing the OS cannot
/// infer. The navigation bar is deliberately left alone — the bottom of this app is its own
/// Shell tab bar on the page background, and the system gesture pill already reads on it.
/// </remarks>
public static class SystemBars
{
    /// <summary>
    /// Sets the status bar icon appearance from the app's current theme: dark icons on a light
    /// app, light icons on a dark one. Safe to call repeatedly and safe to call before the
    /// window handler exists, where it does nothing.
    /// </summary>
    public static void Apply()
    {
#if ANDROID
        // AppearanceLightStatusBars arrived in Android 6.0 / API 23; this app supports API 21.
        // The toolkit's guard is an analyzer contract (CA1416), not a runtime check, so on
        // 21-22 the call would be made against an API that cannot honour it. Returning early
        // leaves those two versions on the platform default, which is the pre-existing
        // behaviour — Android 5 never had per-icon tinting to set in the first place.
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
#elif IOS
        // No guard here, deliberately, and the warning is switched off for this one line rather
        // than left in the log.
        //
        // The toolkit attributes SetStyle to iOS 15 (StatusBar.ios.cs), and this project targets
        // iOS 15 minimum, so the API is always available on every device this app can install on.
        // CA1416 reports it anyway because its reachability analysis works on the preprocessor
        // shape and cannot see that the project's minimum already excludes the unsupported range.
        // A runtime guard on IsIOSVersionAtLeast(15) does not silence it either: the analyzer
        // treats the call as still reachable. The only alternatives were a NoWarn, which would
        // hide every future CA1416 in this file too, or a preprocessor split per target version,
        // which cannot be expressed. So the check is stated here and suppressed narrowly.
#pragma warning disable CA1416 // iOS 15 minimum == the version the toolkit attributes this API to
#endif

#if ANDROID || IOS
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

        // DarkContent = dark glyphs for a light bar. LightContent = white glyphs for a dark
        // bar. Default is deliberately NOT used: it defers to the system theme, which is
        // precisely the mismatch this method exists to correct.
        StatusBar.SetStyle(isDark ? StatusBarStyle.LightContent : StatusBarStyle.DarkContent);
#pragma warning restore CA1416
#endif
    }
}

