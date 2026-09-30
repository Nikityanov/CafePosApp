using CafePos.Core.Data;
using CafePos.Core.Schema.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Schema;

/// <summary>Outcome of a schema upgrade, used by diagnostics and the settings screen.</summary>
public sealed record SchemaMigrationResult(int FinalVersion, bool FreshDatabase, IReadOnlyList<string> AppliedMigrations);

/// <summary>
/// Applies ordered schema migrations to the local SQLite database.
/// Replaces the previous combination of EnsureCreated() + ad-hoc ALTER TABLE statements
/// and the EF migrations folder that was never applied and referenced deleted models.
/// </summary>
public sealed class SchemaMigrator(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<SchemaMigrator> logger)
{
    public static readonly IReadOnlyList<ISchemaMigration> AllMigrations =
    [
        new Migration001_Baseline(),
        new Migration002_MoneyToKopecks(),
        new Migration003_UtcTimestamps(),
        new Migration004_OrderNumbering(),
        new Migration005_AuditDraftsSoftDelete()
    ];

    public int LatestVersion => AllMigrations.Max(migration => migration.Version);

    public async Task<SchemaMigrationResult> MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var tableCount = await SqliteSchemaHelper.ScalarLongAsync(db,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            cancellationToken).ConfigureAwait(false);

        if (tableCount == 0)
        {
            logger.LogInformation("Fresh database: creating schema from the current model");
            await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            foreach (var migration in AllMigrations) db.SchemaVersions.Add(CreateRow(migration));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new SchemaMigrationResult(LatestVersion, true, AllMigrations.Select(m => m.Name).ToList());
        }

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE TABLE IF NOT EXISTS [SchemaVersions] ([Version] INTEGER NOT NULL CONSTRAINT [PK_SchemaVersions] PRIMARY KEY, [Name] TEXT NOT NULL, [AppliedAt] TEXT NOT NULL)",
            cancellationToken).ConfigureAwait(false);

        var appliedVersions = await db.SchemaVersions.AsNoTracking()
            .Select(version => version.Version)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var appliedNames = new List<string>();
        foreach (var migration in AllMigrations.OrderBy(item => item.Version))
        {
            if (appliedVersions.Contains(migration.Version)) continue;

            logger.LogInformation("Applying schema migration {Version}: {Name}", migration.Version, migration.Name);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await migration.ApplyAsync(db, cancellationToken).ConfigureAwait(false);
            db.SchemaVersions.Add(CreateRow(migration));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            appliedNames.Add(migration.Name);
        }

        if (appliedNames.Count > 0)
        {
            logger.LogInformation("Schema upgraded to version {Version} ({Count} migrations)", LatestVersion, appliedNames.Count);
        }

        return new SchemaMigrationResult(LatestVersion, false, appliedNames);
    }

    private SchemaVersion CreateRow(ISchemaMigration migration) => new()
    {
        Version = migration.Version,
        Name = migration.Name,
        AppliedAt = timeProvider.GetUtcNow()
    };
}
