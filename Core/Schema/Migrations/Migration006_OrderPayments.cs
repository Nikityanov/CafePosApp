using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 6 — payment ledger: Orders.PaidKopecks plus the OrderPayments table.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration006_OrderPayments : ISchemaMigration
{
    public int Version => 6;
    public string Name => "Order payments and paid amount";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // 1. The scalar. A literal DEFAULT 0 keeps this statement O(1) — no table rewrite, and no
        //    second pass to fill the existing rows.
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "PaidKopecks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // 2. The ledger. An empty table: instant.
        await MigrationSql.CreateTableAsync(db, "OrderPayments",
            "Id TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, AmountKopecks INTEGER NOT NULL DEFAULT 0, " +
            "Method TEXT NOT NULL, PaidAt TEXT NOT NULL, " +
            "FOREIGN KEY (OrderId) REFERENCES Orders(Id) ON DELETE CASCADE",
            cancellationToken);

        /// <summary>3.</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [Orders] SET [PaidKopecks] = [TotalKopecks] WHERE [PaidKopecks] = 0 AND CAST([Status] AS TEXT) NOT IN ('Cancelled', '3')",
            cancellationToken);

        /// <summary>One synthetic row per backfilled order, so the ledger closes over history and no order reads as "paid by an unrecorded cash payment".</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        await SqliteSchemaHelper.ExecuteAsync(db,
            "INSERT INTO [OrderPayments] ([Id], [OrderId], [AmountKopecks], [Method], [PaidAt]) " +
            "SELECT lower(hex(randomblob(16))), [Id], [TotalKopecks], 'Cash', [CreatedAt] FROM [Orders] " +
            "WHERE [PaidKopecks] > 0 AND CAST([Status] AS TEXT) NOT IN ('Cancelled', '3') " +
            "AND [Id] NOT IN (SELECT [OrderId] FROM [OrderPayments])",
            cancellationToken);

        /// <summary>4. The index the EF model declares. The model/migration parity test does not look at indexes, so without this a fresh install (EnsureCreated) would get one and an upgraded one would not — an invisible divergence.</summary>

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_OrderPayments_OrderId_PaidAt] ON [OrderPayments] ([OrderId], [PaidAt])",
            cancellationToken);
    }
}
