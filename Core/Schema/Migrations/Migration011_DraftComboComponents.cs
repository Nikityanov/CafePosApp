using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 11 — the parked cart gets what the order got in version 10:
/// <c>DraftOrders.OrderType</c>, <c>DraftOrders.CustomerPhone</c>, <c>DraftOrders.RequestedAt</c> and
/// the <c>DraftOrderItemComponents</c> table.
/// </summary>
/// <para>
/// <b>WHY A VERSION OF ITS OWN AND NOT PART OF 10.</b> None of these columns depends on a bundle
/// table: a café that never sells a bundle still needs its parked carts to keep the phone and the
/// requested time the operator already entered. Keeping the mirror in its own version means the two
/// sides can be read apart — version 10 is what a fiscal document is built from, version 11 is a
/// crash-safety net — and an operator who rolls back to a build without bundles keeps working carts
/// instead of losing them.
/// </para>
/// <para>
/// <b>THE REASON A PARKED CART KEEPS THE PHONE AT ALL, WHEN THE ORDER DROPS IT FOR COUNTER SERVICE.</b>
/// 152-ФЗ ст. 6(1)(5) allows a phone to be processed only where it is needed to perform the contract,
/// and a parked counter-service order does not need one: it is dropped when the checkout writes the
/// order, exactly as <see cref="Migration010_CombosAndOrderFields"/> states. A draft keeps it only
/// because the operator is going to come back to this cart and finish it — losing a number they
/// already typed for a takeaway would make them type it again, and the second time it is typed it is
/// the one that is wrong. The data reaches no fiscal document until the cart becomes an order, and
/// that is where the decision is applied.
/// </para>
/// <para>
/// Each statement is a bare ADD COLUMN with a literal default or NULL, so every one of them is O(1):
/// SQLite never rewrites the table for such a column, which matters on a device whose whole database
/// lives in a phone's flash.
/// </para>
/// </remarks>
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
