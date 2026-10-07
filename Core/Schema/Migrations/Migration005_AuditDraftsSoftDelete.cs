using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 5 — parked carts (crash-safe drafts), stock movement journal, order status history and soft delete for products.</summary>

internal sealed class Migration005_AuditDraftsSoftDelete : ISchemaMigration
{
    public int Version => 5;
    public string Name => "Draft orders, stock movements, status history, soft delete";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.CreateTableAsync(db, "DraftOrders",
            "Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, ShiftId TEXT NULL, " +
            "IsActiveCart INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await MigrationSql.CreateTableAsync(db, "DraftOrderItems",
            "Id TEXT NOT NULL PRIMARY KEY, DraftOrderId TEXT NOT NULL, ProductId TEXT NOT NULL, ProductName TEXT NOT NULL, " +
            "PriceKopecks INTEGER NOT NULL DEFAULT 0, SelectedModifierName TEXT NULL, SelectedVariantName TEXT NULL, " +
            "Quantity INTEGER NOT NULL DEFAULT 0, " +
            "FOREIGN KEY (DraftOrderId) REFERENCES DraftOrders(Id) ON DELETE CASCADE",
            cancellationToken);
        await MigrationSql.CreateTableAsync(db, "StockMovements",
            "Id TEXT NOT NULL PRIMARY KEY, IngredientId TEXT NOT NULL, QuantityDelta TEXT NOT NULL DEFAULT 0, " +
            "StockAfter TEXT NOT NULL DEFAULT 0, Reason TEXT NOT NULL DEFAULT '', OrderId TEXT NULL, CreatedAt TEXT NOT NULL, " +
            "FOREIGN KEY (IngredientId) REFERENCES Ingredients(Id) ON DELETE CASCADE",
            cancellationToken);
        await MigrationSql.CreateTableAsync(db, "OrderStatusHistory",
            "Id TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, Status TEXT NOT NULL, ChangedAt TEXT NOT NULL, Comment TEXT NULL, " +
            "FOREIGN KEY (OrderId) REFERENCES Orders(Id) ON DELETE CASCADE",
            cancellationToken);

        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "IsDeleted", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "DeletedAt", "TEXT NULL", cancellationToken);

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_StockMovements_IngredientId_CreatedAt] ON [StockMovements] ([IngredientId], [CreatedAt])",
            cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_OrderStatusHistory_OrderId_ChangedAt] ON [OrderStatusHistory] ([OrderId], [ChangedAt])",
            cancellationToken);
    }
}
