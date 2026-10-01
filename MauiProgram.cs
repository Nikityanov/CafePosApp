using CafePos.Core.Data;
using CafePos.Core.DependencyInjection;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePosApp.ViewModels;
using CafePosApp.Views;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace CafePosApp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var appDataDirectory = FileSystem.AppDataDirectory;
            var logDirectory = Path.Combine(appDataDirectory, "logs");

            // Logging has to be available before the DI container exists.
            AppLog.Start(logDirectory);
            AppLog.Info($"=== Application start: {DeviceInfo.Platform} {DeviceInfo.VersionString}, {DeviceInfo.Manufacturer} {DeviceInfo.Model} ===");

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                AppLog.Exception("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Exception("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            var logWriter = new LogFileWriter(logDirectory);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider(logWriter));
            builder.Logging.SetMinimumLevel(LogLevel.Information);
#if DEBUG
            builder.Logging.AddDebug();
#endif

            // Platform independent core: database, schema migrations, services.
            builder.Services.AddCafePosCore(new DatabaseOptions
            {
                DatabasePath = Path.Combine(appDataDirectory, "cafe-pos.db3"),
                BackupDirectory = Path.Combine(appDataDirectory, "backups"),
                BackupRetention = 10,
                AutomaticBackupInterval = TimeSpan.FromHours(24),
                SeedDemoData = true
            });

            // Platform services.
            builder.Services.AddSingleton<INavigationService, NavigationService>();
            builder.Services.AddSingleton<IDialogService, DialogService>();
            builder.Services.AddSingleton<IFileService, FileService>();
            builder.Services.AddSingleton<IHapticService, HapticService>();
            builder.Services.AddSingleton<AppSettings>();
            builder.Services.AddSingleton<IModifierPicker, ModifierPicker>();
            builder.Services.AddSingleton<IVariantPicker, VariantPicker>();
            builder.Services.AddSingleton<IDraftPicker, DraftPicker>();
            builder.Services.AddSingleton<ICatalogActionSheet, CatalogActionSheet>();
            builder.Services.AddSingleton<IPaymentSheet, PaymentSheet>();

            // ViewModels.
            builder.Services.AddTransient<MenuViewModel>();
            builder.Services.AddTransient<OrdersViewModel>();
            builder.Services.AddTransient<OrderDetailsViewModel>();
            builder.Services.AddTransient<ShiftReportViewModel>();
            builder.Services.AddTransient<ShiftAnalyticsViewModel>();
            builder.Services.AddTransient<CatalogManagementViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();
            builder.Services.AddTransient<CatalogFormViewModel>();
            builder.Services.AddTransient<ProductFormViewModel>();
            builder.Services.AddTransient<CategoryFormViewModel>();
            builder.Services.AddTransient<ModifierFormViewModel>();
            builder.Services.AddTransient<IngredientFormViewModel>();

            // Shell and pages.
            builder.Services.AddSingleton<AppShell>();
            builder.Services.AddTransient<MenuPage>();
            builder.Services.AddTransient<OrdersPage>();
            builder.Services.AddTransient<OrderDetailsPage>();
            builder.Services.AddTransient<ShiftReportPage>();
            builder.Services.AddTransient<ShiftAnalyticsPage>();
            builder.Services.AddTransient<CatalogManagementPage>();
            builder.Services.AddTransient<SettingsPage>();
            builder.Services.AddTransient<CatalogFormsPage>();

            var app = builder.Build();
            AppLog.Info("MAUI app built successfully");
            return app;
        }
    }
}
