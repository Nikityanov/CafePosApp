using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 14 — StockMovements.Kind INTEGER NOT NULL: what a journal row is, so nothing decides it by reading Reason.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration014_StockMovementKind : ISchemaMigration
{
    public int Version => 14;
    public string Name => "A stock movement records what kind of row it is";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // DEFAULT 0 is StockMovementKind.Unknown, and it is the honest value for a row that predates
        // the column: the journal reading it then refuses rather than assuming what it was.
        await MigrationSql.AddColumnIfMissingAsync(
            db, "StockMovements", "Kind", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
    }
}
