using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Shared SQL helpers so each migration stays declarative and idempotent.</summary>
internal static class MigrationSql
{
    public static async Task CreateTableAsync(AppDbContext db, string table, string body, CancellationToken cancellationToken)
    {
        if (await SqliteSchemaHelper.TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return;
        await SqliteSchemaHelper.ExecuteAsync(db, $"CREATE TABLE [{table}] ({body})", cancellationToken).ConfigureAwait(false);
    }

    public static async Task AddColumnIfMissingAsync(AppDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaHelper.TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return;
        if (await SqliteSchemaHelper.ColumnExistsAsync(db, table, column, cancellationToken).ConfigureAwait(false)) return;
        await SqliteSchemaHelper.ExecuteAsync(db, $"ALTER TABLE [{table}] ADD COLUMN [{column}] {definition}", cancellationToken).ConfigureAwait(false);
    }

    public static async Task DropColumnIfExistsAsync(AppDbContext db, string table, string column, CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaHelper.TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return;
        if (!await SqliteSchemaHelper.ColumnExistsAsync(db, table, column, cancellationToken).ConfigureAwait(false)) return;
        await SqliteSchemaHelper.ExecuteAsync(db, $"DROP INDEX IF EXISTS [IX_{table}_{column}]", cancellationToken).ConfigureAwait(false);
        try
        {
            await SqliteSchemaHelper.ExecuteAsync(db, $"ALTER TABLE [{table}] DROP COLUMN [{column}]", cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Very old SQLite builds cannot drop columns; leaving a stale unused column
            // is harmless because the EF model no longer maps it.
        }
    }

    /// <summary>Converts a legacy TEXT money column (rubles, e.g. "220.5") into an INTEGER kopeck column. Safe to call repeatedly: it does nothing when the target column already exists or when the source column is already an integer.</summary>

    public static async Task ConvertMoneyColumnAsync(AppDbContext db, string table, string oldColumn, string newColumn, CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaHelper.TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return;
        var columns = await SqliteSchemaHelper.GetColumnsAsync(db, table, cancellationToken).ConfigureAwait(false);
        var target = columns.FirstOrDefault(c => string.Equals(c.Name, newColumn, StringComparison.OrdinalIgnoreCase));
        if (target is not null) return;

        var source = columns.FirstOrDefault(c => string.Equals(c.Name, oldColumn, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;
        if (source.Type.Contains("INT", StringComparison.OrdinalIgnoreCase)) return;

        await SqliteSchemaHelper.ExecuteAsync(db, $"ALTER TABLE [{table}] ADD COLUMN [{newColumn}] INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await SqliteSchemaHelper.ExecuteAsync(
            db,
            $"UPDATE [{table}] SET [{newColumn}] = CAST(ROUND(CAST([{oldColumn}] AS REAL) * 100) AS INTEGER) WHERE [{oldColumn}] IS NOT NULL",
            cancellationToken).ConfigureAwait(false);
        await DropColumnIfExistsAsync(db, table, oldColumn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rewrites legacy local timestamps (no offset suffix) as UTC ISO-8601 values with an explicit "+00:00" offset, so the provider can never misread them as local time.</summary>

    public static async Task ConvertToUtcAsync(AppDbContext db, string table, string column, CancellationToken cancellationToken)
    {
        if (!await SqliteSchemaHelper.TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return;
        var source = (await SqliteSchemaHelper.GetColumnsAsync(db, table, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        if (source is null) return;

        await SqliteSchemaHelper.ExecuteAsync(
            db,
            $"UPDATE [{table}] SET [{column}] = strftime('%Y-%m-%d %H:%M:%f+00:00', [{column}], 'utc') " +
            $"WHERE [{column}] IS NOT NULL AND [{column}] NOT LIKE '%+%' AND [{column}] NOT LIKE '%Z%'",
            cancellationToken).ConfigureAwait(false);
    }
}
