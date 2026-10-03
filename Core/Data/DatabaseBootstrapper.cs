using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Core.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Data;

/// <summary>
/// Prepares the local database on startup: schema migration, active shift, demo data.
/// <para>
/// <see cref="InitializeAsync"/> is the single shared entry point: N callers (App startup plus
/// every page that reads SQLite) all await ONE run instead of racing their own. Two guarantees
/// matter to callers:
/// </para>
/// <list type="bullet">
/// <item>Concurrent callers get the same in-flight task, and a completed run is never repeated.</item>
/// <item>A <b>failed</b> run is evicted from the cache, so the next caller retries.</item>
/// </list>
/// <para>
/// Retry (rather than caching the failure and surfacing it forever) is the deliberate choice for
/// this app: it is an offline single-device POS, the usual startup failures are transient
/// (file lock, disk busy, antivirus) and the operator needs the till to come up on the next tap.
/// Caching a fault would leave the app permanently broken until a reinstall, with no operator
/// action able to clear it. The failure is still surfaced — it is thrown to the caller and
/// logged — it is simply not latched, so navigating to another tab retries the migration.
/// </para>
/// </summary>
public sealed class DatabaseBootstrapper(
    IDbContextFactory<AppDbContext> factory,
    SchemaMigrator migrator,
    DatabaseOptions options,
    IBackupService backupService,
    ILogger<DatabaseBootstrapper> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Task<SchemaMigrationResult>? initialization;

    /// <summary>
    /// Awaits the shared initialization run, starting it if this is the first caller.
    /// Safe to call from any number of pages at the same time and from application startup.
    /// </summary>
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
            // Drop the failed attempt under the gate so the next caller starts a fresh one.
            // The re-read of the field (not the captured local) matters: if another caller
            // already replaced it, their in-flight run is left alone.
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

        // NO SHIFT IS CREATED HERE, and removing that call is one of the three places this change
        // touches. Startup used to guarantee an active shift, which meant a terminal that was merely
        // RESTARTED came back with a shift that nobody opened and a drawer nobody counted — and no
        // place to record the change in it, because the shift already existed. A terminal between
        // shifts is now a normal state that the opening screen handles.
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
