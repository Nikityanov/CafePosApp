using CafePos.Core.Data;
using CafePos.Core.Schema;
using CafePos.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CafePos.Core.DependencyInjection;

/// <summary>
/// Registers everything that does not depend on a UI framework. The MAUI project only adds
/// platform services (navigation, dialogs, files) and the ViewModels on top of this.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCafePosCore(this IServiceCollection services, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<AppDbContext>(builder => builder
            .UseSqlite(options.ConnectionString)
            .EnableDetailedErrors());

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<DatabaseBootstrapper>();

        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<ICheckoutService, CheckoutService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<ICashLedgerService, CashLedgerService>();
        services.AddSingleton<IShiftSession, ShiftSession>();
        services.AddSingleton<IDraftOrderService, DraftOrderService>();
        services.AddSingleton<IReportExportService, ReportExportService>();
        services.AddSingleton<IBackupService, BackupService>();

        return services;
    }
}
