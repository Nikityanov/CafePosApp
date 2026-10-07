using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CafePos.Core.DependencyInjection;

/// <summary>Registers everything that does not depend on a UI framework. The MAUI project only adds platform services (navigation, dialogs, files) and the ViewModels on top of this.</summary>

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCafePosCore(this IServiceCollection services, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // One assignment, once, at startup: the promise an order without a requested time is given.
        // Почему так — `docs/decisions/schema.md`

        Order.LeadTimeMinutes = options.LeadTimeMinutes;

        services.AddDbContextFactory<AppDbContext>(builder => builder
            .UseSqlite(options.ConnectionString)
            .EnableDetailedErrors());

        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<DatabaseBootstrapper>();

        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IComboService, ComboService>();
        /// <summary>One OrderService registered as the concrete singleton and re-published under each port it satisfies, rather than seven separate registrations that wou…</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

services.AddSingleton<OrderService>();
services.AddSingleton<IOrderService>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IOrderOperations>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IOrderQueries>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IOrderCommands>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IOrderPayments>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IShiftLedger>(sp => sp.GetRequiredService<OrderService>());
services.AddSingleton<IOrderReporting>(sp => sp.GetRequiredService<OrderService>());
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
