using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Core.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Data;

/// <summary>Prepares the local database on startup: schema migration, active shift, demo data.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed class DatabaseBootstrapper(
    IDbContextFactory<AppDbContext> factory,
    SchemaMigrator migrator,
    DatabaseOptions options,
    IBackupService backupService,
    ILogger<DatabaseBootstrapper> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Task<SchemaMigrationResult>? initialization;

    /// <summary>Awaits the shared initialization run, starting it if this is the first caller. Safe to call from any number of pages at the same time and from application startup.</summary>

    public async Task<SchemaMigrationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            initialization ??= InitializeCoreAsync();
        }
        finally
        {
            gate.Release();
        }

        try
        {
            return await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            /// <summary>Drop the failed attempt under the gate so the next caller starts a fresh one. The re-read of the field (not the captured local) matters: if another caller already replaced it, their in-flight run is left alone.</summary>

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (initialization is { IsFaulted: true }) initialization = null;
            }
            finally
            {
                gate.Release();
            }

            throw;
        }
    }

    private async Task<SchemaMigrationResult> InitializeCoreAsync()
    {
        var result = await migrator.MigrateAsync(CancellationToken.None).ConfigureAwait(false);

        // NO SHIFT IS CREATED HERE, and removing that call is one of the three places this change touches.
        // Почему так — `docs/decisions/schema.md`

        await using var db = await factory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        if (options.SeedDemoData) await DemoDataSeeder.SeedIfEmptyAsync(db, CancellationToken.None).ConfigureAwait(false);

        // Daily safety copy: never block startup if the backup cannot be written.
        try
        {
            var backup = await backupService.EnsureAutomaticBackupAsync(CancellationToken.None).ConfigureAwait(false);
            if (backup is not null) logger.LogInformation("Automatic backup stored: {FileName}", backup.FileName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Automatic backup failed; startup continues");
        }

        logger.LogInformation("Database ready. Schema version {Version}", result.FinalVersion);
        return result;
    }
}
