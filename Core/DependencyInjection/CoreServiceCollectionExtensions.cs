using CafePos.Core.Data;
using CafePos.Core.Models;
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

        // One assignment, once, at startup: the promise an order without a requested time is given.
        // It is ambient (see Order.LeadTimeMinutes) rather than injected because PromisedAt is read
        // straight from markup with nowhere to receive a parameter, and it is read at startup so that
        // a promise computed later cannot disagree with the menu that quoted it.
        Order.LeadTimeMinutes = options.LeadTimeMinutes;

        services.AddDbContextFactory<AppDbContext>(builder => builder
            .UseSqlite(options.ConnectionString)
            .EnableDetailedErrors());

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<DatabaseBootstrapper>();

        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IComboService, ComboService>();
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<ICheckoutService, CheckoutService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IContactDataService, ContactDataService>();
        services.AddSingleton<ICashLedgerService, CashLedgerService>();
        services.AddSingleton<IShiftSession, ShiftSession>();
        services.AddSingleton<IDraftOrderService, DraftOrderService>();
        services.AddSingleton<IReportExportService, ReportExportService>();
        services.AddSingleton<IBackupService, BackupService>();

        return services;
    }
}
