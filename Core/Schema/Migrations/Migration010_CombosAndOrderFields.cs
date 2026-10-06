using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 10 — bundles and the new order-level facts: the <c>Combos</c>, <c>ComboComponents</c>
/// and <c>OrderItemComponents</c> tables, <c>Orders.OrderType</c>, <c>Orders.CustomerPhone</c>,
/// <c>Orders.RequestedAt</c> and <c>OrderItems.ListPriceKopecks</c>.
/// </summary>
/// <para>
/// <b>THE THREE NEW TABLES ARE CREATED EMPTY, AND THAT IS THE ONLY HONEST BACKFILL.</b> Nothing here
/// can be reconstructed for a sale that already happened: the composition of a line was never stored,
/// and inventing one would put a fictional list of dishes into the exact table a fiscal receipt is
/// printed from. An empty table reads as "this terminal did not record compositions before version
/// 10", which is true. The same reasoning made <c>CashMovements</c> (version 9) empty: a reconstructed
/// history is worse than an acknowledged gap, because nobody can tell afterwards which parts of it
/// were made up.
/// </para>
/// <para>
/// <b>ListPriceKopecks = PriceKopecks</b>, for every existing line, and the guard is the point. Until
/// this column existed the stored price WAS the allowed price — nothing could change one without
/// changing the other — so copying one onto the other states a fact, while leaving 0 behind would
/// make every historical line look like a 100% discount in the shift report's discount section. Old
/// orders can therefore never show a phantom discount.
/// </para>
/// <para>
/// <b>OrderType defaults to 'CounterService' and needs no UPDATE pass.</b> A NOT NULL column with a
/// literal default IS the backfill, the same argument version 7 makes for IsRefund. It is an
/// assumption rather than a fact — nobody recorded whether an old order was eaten here or taken away —
/// but it is the conservative one and it is safe in the only way an assumption about a phone can be:
/// those orders have no phone to lose and no promised time to mis-sort.
/// </para>
/// <para>
/// Every index the EF model declares is created explicitly, because the model/migration parity test
/// reads COLUMNS and not indexes. Without these statements an upgraded terminal would have the tables
/// with no index while a fresh one got them from EnsureCreated, and that divergence is invisible
/// until the day the catalogue is large enough to notice.
/// </para>
/// </remarks>
internal sealed class Migration010_CombosAndOrderFields : ISchemaMigration
{
    public int Version => 10;
    public string Name => "Combos, order type, requested time, customer phone, list price";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // 1. The catalogue side: the bundle, then the slots that belong to it.
        await MigrationSql.CreateTableAsync(db, "Combos",
            "Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, " +
            "IsDeleted INTEGER NOT NULL DEFAULT 0, SortOrder INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await MigrationSql.CreateTableAsync(db, "ComboComponents",
            "Id TEXT NOT NULL PRIMARY KEY, ComboId TEXT NOT NULL, ProductId TEXT NOT NULL, " +
            "QuantityPerUnit INTEGER NOT NULL DEFAULT 1, ComponentPriceKopecks INTEGER NULL, SubstituteProductId TEXT NULL, " +
            "FOREIGN KEY (ComboId) REFERENCES Combos(Id) ON DELETE CASCADE, " +
            // RESTRICT, not CASCADE: a dish inside a bundle must not vanish and leave a slot pointing
            // at nothing. Products are soft-deleted, so this fires only on a hand-edited database.
            "FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE RESTRICT, " +
            // No ON DELETE clause on the substitute, because EF maps this optional relationship as
            // ClientSetNull (database NO ACTION, the client nulls the reference) and the constraint
            // here has to say the same thing a fresh install's DDL says.
            "FOREIGN KEY (SubstituteProductId) REFERENCES Products(Id)",
            cancellationToken);

        // 2. The sale side. ProductId here is a snapshot reference with no FOREIGN KEY — see the
        //    OrderItemComponent type remarks, the row has to outlive the catalogue entry.
        await MigrationSql.CreateTableAsync(db, "OrderItemComponents",
            "Id TEXT NOT NULL PRIMARY KEY, OrderItemId TEXT NOT NULL, ProductId TEXT NOT NULL, ProductName TEXT NOT NULL, " +
            "QuantityPerUnit INTEGER NOT NULL DEFAULT 1, UnitPriceKopecks INTEGER NOT NULL DEFAULT 0, " +
            "ReferencePriceKopecks INTEGER NOT NULL DEFAULT 0, " +
            "SortOrder INTEGER NOT NULL DEFAULT 0, " +
            "FOREIGN KEY (OrderItemId) REFERENCES OrderItems(Id) ON DELETE CASCADE",
            cancellationToken);

        // 3. The order-level facts. CustomerPhone is 24 characters because E.164 caps a number at 15
        //    digits plus the '+'; the value is written by PhoneNumber.Normalize and never typed free.
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "OrderType", "TEXT NOT NULL DEFAULT 'CounterService'", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "CustomerPhone", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "RequestedAt", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "OrderItems", "ListPriceKopecks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

        // 4. The one backfill there is. The literal DEFAULT 0 kept the statement above O(1) — SQLite
        //    does not rewrite the table for a constant-defaulted column — so the rows still carry 0
        //    and this is where they learn their list price. WHERE ListPriceKopecks = 0 makes a re-run
        //    after a partial failure harmless, and it cannot touch a line somebody has already
        //    reviewed: a reviewed line has a non-zero list price, which is the whole signal.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [OrderItems] SET [ListPriceKopecks] = [PriceKopecks] WHERE [ListPriceKopecks] = 0",
            cancellationToken);

        // 5. The indexes the EF model declares. An index on a foreign key here is not decoration:
        //    ComboId is how a bundle's composition is read, ProductId is how an 86 report finds the
        //    slots that use a dish, OrderItemId is how a receipt is reprinted, and RequestedAt is
        //    what the schedule section reads.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_ComboComponents_ComboId] ON [ComboComponents] ([ComboId])",
            cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_ComboComponents_ProductId] ON [ComboComponents] ([ProductId])",
            cancellationToken);
        // EF indexes every foreign key by convention, so the model has one on SubstituteProductId too.
        // This statement is not here because substitute lookups are hot — they are not — but because a
        // fresh install would get that index from EnsureCreated and an upgraded one would not, and the
        // parity test reads columns rather than indexes, so nothing would ever report the difference.
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_ComboComponents_SubstituteProductId] ON [ComboComponents] ([SubstituteProductId])",
            cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_OrderItemComponents_OrderItemId] ON [OrderItemComponents] ([OrderItemId])",
            cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_Orders_RequestedAt] ON [Orders] ([RequestedAt])",
            cancellationToken);
    }
}
