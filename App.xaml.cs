using CafePos.Core.Data;
using CafePos.Core.Services;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePos.Presentation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp
{
    public partial class App : Application
    {
        private readonly AppShell shell;
        private readonly DatabaseBootstrapper bootstrapper;
        private readonly IShiftSession shiftSession;
        private readonly INavigationService navigation;

        public App(
            IServiceProvider services,
            IAppSettings settings,
            DatabaseBootstrapper bootstrapper,
            IShiftSession shiftSession,
            INavigationService navigation)
        {
            AppLog.Info("App constructor started");
            InitializeComponent();
            settings.ApplyTheme();

            // Before the shell is built, so the very first frame already prices in the stored
            // currency instead of painting ₽ and correcting itself a moment later.
            CurrencySelection.Reload();
            shell = services.GetRequiredService<AppShell>();
            this.bootstrapper = bootstrapper;
            this.shiftSession = shiftSession;
            this.navigation = navigation;
            AppLog.Info("AppShell resolved successfully");
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            AppLog.Info("CreateWindow started");

            // CreateWindow is the first hook that runs after the DI container is complete but
            // before any ShellContent is realised, so the migration is under way before the
            // first page can query. It is started, not awaited: blocking here would keep the
            // operator looking at a blank window for the whole migration on a large catalogue.
            // Correctness does not depend on this call winning the race — every page that reads
            // SQLite awaits the same shared DatabaseBootstrapper task first, and the bootstrapper
            // collapses concurrent callers onto a single run.
            BeginDatabaseInitialization();

            var window = new Window(shell);

            // Status bar icon appearance. MAUI derives it from the Material theme colour and
            // gets this app wrong — see Services/SystemBars for the measurement and for why
            // Android resources cannot express the fix.
            //
            // HandlerChanged, not the constructor: the platform view does not exist yet when
            // Window is constructed, so a call here would be a no-op. This fires once the
            // window is realised, which is the first moment the setting can actually stick.
            window.HandlerChanged += OnWindowHandlerChanged;

            // Reload the currency HERE as well as in the constructor. CreateWindow is not
            // guaranteed to run before the first page is realised in every launch path, and a
            // stale cache would paint the wrong symbol until something else refreshed it.
            CurrencySelection.Reload();

            // The app has an in-app theme picker, so the status bar has to follow the APP's
            // theme, not the system's. RequestedThemeChanged covers all three choices the
            // Picker offers — «Системная» included, since RequestedTheme already resolves
            // Unspecified through to the system value.
            //
            // The -= before the += is what keeps this idempotent across repeated
            // CreateWindow calls, and it is also why nothing detaches this on OnSleep: an
            // earlier version did, which silently killed the subscription the first time the
            // till was backgrounded and restored — the status bar then stopped following the
            // theme at all. Measured on the emulator: after a Home-and-return, switching back
            // to «Светлая» left white icons on a light page. Application is a singleton, so
            // there is exactly one handler for the process lifetime and no leak to avoid.
            RequestedThemeChanged -= OnRequestedThemeChanged;
            RequestedThemeChanged += OnRequestedThemeChanged;

            return window;
        }

        private void OnWindowHandlerChanged(object? sender, EventArgs e) => SystemBars.Apply();

        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => SystemBars.Apply();

        private void BeginDatabaseInitialization()
        {
            _ = InitializeDatabaseAsync();
        }

        private async Task InitializeDatabaseAsync()
        {
            try
            {
                await bootstrapper.InitializeAsync();

                // Only NOW can the answer be known, and it decides where the operator lands: a first
                // launch, a restart, and a terminal between shifts all arrive here with no shift open,
                // and all three are states the app used to paper over by creating one silently.
                await shiftSession.RefreshAsync();
                if (!shiftSession.IsShiftOpen) OpenShiftScreenAsync();

                AppLog.Info("Database initialization completed");
            }
            catch (Exception exception)
            {
                // Not fatal, and not latched: the bootstrapper evicts a failed run from its
                // cache, so the first page load (or any later tab) retries the migration.
                AppLog.Exception("Startup database initialization failed; pages will retry", exception);
            }
        }

        /// <summary>
        /// Shows the opening screen from a startup task that knows nothing about Shell's state.
        /// </summary>
        /// <remarks>
        /// Dispatched, because this runs while the window is still being built: <c>GoToAsync</c> on a
        /// Shell whose first navigation has not finished is a no-op that reports success, and the
        /// operator would be left on the menu of a till with no shift, guarded by
        /// <c>AppShell.OnNavigating</c> but with nothing on screen explaining why.
        /// <para>
        /// Deliberately NOT awaited — the caller is already a fire-and-forget task, and holding the
        /// startup continuation open for a dispatcher round-trip would delay the first page load for
        /// no gain.
        /// </para>
        /// </remarks>
        private void OpenShiftScreenAsync() => Dispatcher.Dispatch(() => _ = NavigateToOpenShiftAsync());

        private async Task NavigateToOpenShiftAsync()
        {
            try
            {
                await navigation.GoToOpenShiftAsync();
            }
            catch (Exception exception)
            {
                AppLog.Exception("Could not show the opening screen", exception);
            }
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            AppLog.Info("Application suspended");
        }
    }
}
