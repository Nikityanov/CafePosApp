using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 12 — a bundle states its own price: `Combos.PriceKopecks INTEGER NOT NULL`.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration012_ComboOwnPrice : ISchemaMigration
{
    public int Version => 12;
    public string Name => "A bundle states its own price, backfilled from the sum of its slots";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(
            db, "Combos", "PriceKopecks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // ALTER TABLE silently does nothing when Combos does not exist (MigrationSql guards it), so the
        // UPDATE is guarded the same way rather than throwing on a database that never got version 10.
        if (!await SqliteSchemaHelper.TableExistsAsync(db, "Combos", cancellationToken).ConfigureAwait(false))
            return;

        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [Combos] SET [PriceKopecks] = COALESCE((" +
                "SELECT SUM(COALESCE([cc].[ComponentPriceKopecks], [p].[PriceKopecks]) * [cc].[QuantityPerUnit]) " +
                "FROM [ComboComponents] [cc] " +
                "LEFT JOIN [Products] [p] ON [p].[Id] = [cc].[ProductId] " +
                "WHERE [cc].[ComboId] = [Combos].[Id]), 0) " +
            "WHERE [PriceKopecks] = 0",
            cancellationToken);
    }
}
