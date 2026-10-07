using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 10 — bundles and the new order-level facts: the `Combos`, `ComboComponents` and `OrderItemComponents` tables, `Orders.OrderType`, `Orders.Cust…</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
            // No ON DELETE clause on the substitute, because EF maps this optional relationship as ClientSetNull (database NO ACTION, the client nulls the reference…
            // Почему так — `docs/decisions/schema.md`

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

        /// <summary>4.</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        await SqliteSchemaHelper.ExecuteAsync(db,
            "UPDATE [OrderItems] SET [ListPriceKopecks] = [PriceKopecks] WHERE [ListPriceKopecks] = 0",
            cancellationToken);

        /// <summary>5.</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_ComboComponents_ComboId] ON [ComboComponents] ([ComboId])",
            cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_ComboComponents_ProductId] ON [ComboComponents] ([ProductId])",
            cancellationToken);
        /// <summary>EF indexes every foreign key by convention, so the model has one on SubstituteProductId too.</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
