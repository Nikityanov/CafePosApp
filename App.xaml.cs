using CafePos.Core.Data;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp
{
    public partial class App : Application
    {
        private readonly AppShell shell;
        private readonly DatabaseBootstrapper bootstrapper;

        public App(IServiceProvider services, AppSettings settings, DatabaseBootstrapper bootstrapper)
        {
            AppLog.Info("App constructor started");
            InitializeComponent();
            settings.ApplyTheme();
            shell = services.GetRequiredService<AppShell>();
            this.bootstrapper = bootstrapper;
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

            return new Window(shell);
        }

        private void BeginDatabaseInitialization()
        {
            _ = InitializeDatabaseAsync();
        }

        private async Task InitializeDatabaseAsync()
        {
            try
            {
                await bootstrapper.InitializeAsync();
                AppLog.Info("Database initialization completed");
            }
            catch (Exception exception)
            {
                // Not fatal, and not latched: the bootstrapper evicts a failed run from its
                // cache, so the first page load (or any later tab) retries the migration.
                AppLog.Exception("Startup database initialization failed; pages will retry", exception);
            }
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            AppLog.Info("Application suspended");
        }
    }
}
