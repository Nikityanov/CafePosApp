using CafePos.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Schema;

/// <summary>Low level SQLite introspection helpers used by schema migrations.</summary>
internal static class SqliteSchemaHelper
{
    public static async Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken cancellationToken)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = table;
            command.Parameters.Add(parameter);
            await OpenAsync(db, cancellationToken).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }
    }

    public static async Task<bool> ColumnExistsAsync(AppDbContext db, string table, string column, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(db, table, cancellationToken).ConfigureAwait(false);
        return columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<List<ColumnInfo>> GetColumnsAsync(AppDbContext db, string table, CancellationToken cancellationToken)
    {
        var result = new List<ColumnInfo>();
        if (!await TableExistsAsync(db, table, cancellationToken).ConfigureAwait(false)) return result;

        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = $"PRAGMA table_info([{table}])";
            await OpenAsync(db, cancellationToken).ConfigureAwait(false);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Add(new ColumnInfo(reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        return result;
    }

    public static async Task ExecuteAsync(AppDbContext db, string sql, CancellationToken cancellationToken)
    {
        await OpenAsync(db, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<long> ScalarLongAsync(AppDbContext db, string sql, CancellationToken cancellationToken)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            await OpenAsync(db, cancellationToken).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
    }

    private static async Task OpenAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed record ColumnInfo(string Name, string Type);
}
