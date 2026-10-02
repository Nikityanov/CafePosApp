using Android.App;
using Android.Content.PM;
using Android.OS;
using CafePosApp.Diagnostics;
using CafePosApp.Services;

namespace CafePosApp
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        /// <summary>
        /// Re-asserts the status bar icon appearance every time the app comes to the foreground.
        /// </summary>
        /// <remarks>
        /// This override is the load-bearing one. MAUI re-runs its own
        /// <c>ConfigureTranslucentSystemBars</c> when the window is (re)configured, and it
        /// derives the icon appearance from <c>colorPrimary</c> / <c>colorSurface</c> — this
        /// app's navy #143D59 — so it asks Android for white icons over the #FAFAFA page on
        /// every resume. A setting applied once at startup is therefore reverted the first
        /// time the operator switches away from the till and back.
        ///
        /// <c>OnResume</c> is the last Android lifecycle event before the screen is shown, so
        /// applying here is what actually survives. See Services/SystemBars for why the theme
        /// resources cannot fix this instead.
        ///
        /// The Activity is declared with <c>ConfigChanges.UiMode</c>, so a system light/dark
        /// switch does NOT recreate it and does not re-run <c>OnCreate</c> — which is exactly
        /// why this cannot live in <c>OnCreate</c> and why the app-theme path is handled
        /// separately in App.CreateWindow via RequestedThemeChanged.
        /// </remarks>
        protected override void OnResume()
        {
            base.OnResume();

            try
            {
                SystemBars.Apply();
            }
            catch (Exception exception)
            {
                // Never fatal: a status bar that is hard to read is a cosmetic defect, and
                // taking the process down over one would be far worse than the defect.
                AppLog.Exception("MainActivity.OnResume: status bar appearance not applied", exception);
            }
        }
    }
}
