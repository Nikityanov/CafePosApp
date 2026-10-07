using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 11 — the parked cart gets what the order got in version 10: `DraftOrders.OrderType`, `DraftOrders.CustomerPhone`, `DraftOrders.RequestedAt` an…</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration011_DraftComboComponents : ISchemaMigration
{
    public int Version => 11;
    public string Name => "Parked carts keep composition, order type, phone and requested time";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.CreateTableAsync(db, "DraftOrderItemComponents",
            "Id TEXT NOT NULL PRIMARY KEY, DraftOrderItemId TEXT NOT NULL, ProductId TEXT NOT NULL, ProductName TEXT NOT NULL, " +
            "QuantityPerUnit INTEGER NOT NULL DEFAULT 1, UnitPriceKopecks INTEGER NOT NULL DEFAULT 0, " +
            "ReferencePriceKopecks INTEGER NOT NULL DEFAULT 0, SortOrder INTEGER NOT NULL DEFAULT 0, " +
            "FOREIGN KEY (DraftOrderItemId) REFERENCES DraftOrderItems(Id) ON DELETE CASCADE",
            cancellationToken);

        await MigrationSql.AddColumnIfMissingAsync(db, "DraftOrders", "OrderType", "TEXT NOT NULL DEFAULT 'CounterService'", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "DraftOrders", "CustomerPhone", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "DraftOrders", "RequestedAt", "TEXT NULL", cancellationToken);

        // The index the EF model declares, created explicitly: the parity test reads columns, so a
        // missing index here would leave an upgraded terminal without one and a fresh one with it.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_DraftOrderItemComponents_DraftOrderItemId] ON [DraftOrderItemComponents] ([DraftOrderItemId])",
            cancellationToken);
    }
}
