using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 6 — payment ledger: Orders.PaidKopecks plus the OrderPayments table.
/// <para>
/// Order matters and each step is individually idempotent: the column first (with a literal
/// DEFAULT 0, so SQLite's ADD COLUMN is O(1) and needs no table rewrite), then the empty table,
/// then the backfill. SchemaMigrator wraps a migration in a transaction and SQLite DDL is
/// transactional, so a crash mid-way leaves neither the column, nor the table, nor the version row.
/// </para>
/// <para>
/// WHY the backfill exists, and why it is a product decision rather than an oversight: with
/// DEFAULT 0 every pre-existing open order would silently become a debtor. It would then be
/// refused at Ready, it would inflate the shift-close refusal, and it would fabricate debt that
/// never existed — payment deferral did not exist before this feature, those orders were paid at
/// checkout. So every non-cancelled order is marked fully paid AND gets one matching synthetic
/// Cash row. Both or neither: the column without the row breaks the reconciliation invariant
/// (PaidKopecks == SUM(payments)) and makes every historical order read as "paid by an unrecorded
/// cash payment", which is exactly the hole an auditor flags first. Cancelled orders stay at 0 —
/// they were never collected.
/// </para>
/// </summary>
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

        // 3. Backfill the scalar, then the matching ledger row.
        //
        // The status predicate is written against a CAST because Orders.Status is not stored the
        // same way in every database. Ones created from the current model have a TEXT column, and
        // a text comparison against 'Cancelled' is exact. A database from the first release still
        // has the original `Status INTEGER` column, and there SQLite applies NUMERIC affinity to
        // the text literal: 'Cancelled' becomes 0, so an order whose status was written by that old
        // version as the ordinal 3 is "not cancelled" and gets marked paid with a cash payment it
        // never received — a fabricated entry in the very shift report the backfill feeds. The
        // legacy ordinal of Cancelled is 3, so it is listed next to the name and the CAST removes
        // the affinity from the comparison. Status members are never renamed, so both literals
        // stay stable; an order row of an app that no longer exists can still only be one of the
        // four names or the four ordinals.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [Orders] SET [PaidKopecks] = [TotalKopecks] WHERE [PaidKopecks] = 0 AND CAST([Status] AS TEXT) NOT IN ('Cancelled', '3')",
            cancellationToken);

        // One synthetic row per backfilled order, so the ledger closes over history and no order
        // reads as "paid by an unrecorded cash payment". lower(hex(randomblob(16))) is the 'N'
        // Guid format, which is the one new Guid(string) accepts for a key read back later. The
        // NOT IN guard keeps the statement idempotent: a re-run after a partial failure must not
        // add a second payment to the same order.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "INSERT INTO [OrderPayments] ([Id], [OrderId], [AmountKopecks], [Method], [PaidAt]) " +
            "SELECT lower(hex(randomblob(16))), [Id], [TotalKopecks], 'Cash', [CreatedAt] FROM [Orders] " +
            "WHERE [PaidKopecks] > 0 AND CAST([Status] AS TEXT) NOT IN ('Cancelled', '3') " +
            "AND [Id] NOT IN (SELECT [OrderId] FROM [OrderPayments])",
            cancellationToken);

        // 4. The index the EF model declares. The model/migration parity test does not look at
        //    indexes, so without this a fresh install (EnsureCreated) would get one and an upgraded
        //    one would not — an invisible divergence.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_OrderPayments_OrderId_PaidAt] ON [OrderPayments] ([OrderId], [PaidAt])",
            cancellationToken);
    }
}
