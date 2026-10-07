using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 1 — brings any legacy database (created by the old EnsureCreated + ALTER TABLE hybrid, at any intermediate revision) up to the "structural bas…</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration001_Baseline : ISchemaMigration
{
    public int Version => 1;
    public string Name => "Baseline: legacy columns, current tables, drop dead variant tables";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // Tables introduced after the very first release.
        await MigrationSql.CreateTableAsync(db, "Categories",
            "Id TEXT NOT NULL CONSTRAINT PK_Categories PRIMARY KEY, Name TEXT NOT NULL", cancellationToken);
        await MigrationSql.CreateTableAsync(db, "ProductVariants",
            "Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, PriceKopecks INTEGER NOT NULL DEFAULT 0, " +
            "IsAvailable INTEGER NOT NULL DEFAULT 1, SortOrder INTEGER NOT NULL DEFAULT 0, ProductId TEXT NOT NULL, " +
            "FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE", cancellationToken);
        await MigrationSql.CreateTableAsync(db, "Ingredients",
            "Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Unit TEXT NOT NULL DEFAULT '', " +
            "CostPerUnitKopecks INTEGER NOT NULL DEFAULT 0, StockQuantity TEXT NOT NULL DEFAULT 0, " +
            "MinStockLevel TEXT NOT NULL DEFAULT 0, Supplier TEXT NULL, IsAvailable INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await MigrationSql.CreateTableAsync(db, "RecipeItems",
            "Id TEXT NOT NULL PRIMARY KEY, ProductId TEXT NOT NULL, IngredientId TEXT NOT NULL, Quantity TEXT NOT NULL DEFAULT 0, " +
            "FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE, " +
            "FOREIGN KEY (IngredientId) REFERENCES Ingredients(Id) ON DELETE CASCADE", cancellationToken);
        await MigrationSql.CreateTableAsync(db, "PriceRules",
            "Id TEXT NOT NULL PRIMARY KEY, ProductId TEXT NOT NULL, Name TEXT NOT NULL, DayOfWeek INTEGER NULL, " +
            "StartHour INTEGER NOT NULL, EndHour INTEGER NOT NULL, PriceOverrideKopecks INTEGER NULL, " +
            "PriceAdjustmentKopecks INTEGER NOT NULL DEFAULT 0, PercentAdjustment TEXT NOT NULL DEFAULT 0, " +
            "IsActive INTEGER NOT NULL DEFAULT 1, FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE", cancellationToken);
        await MigrationSql.CreateTableAsync(db, "PriceHistoryEntries",
            "Id TEXT NOT NULL PRIMARY KEY, ProductId TEXT NOT NULL, OldPriceKopecks INTEGER NOT NULL DEFAULT 0, " +
            "NewPriceKopecks INTEGER NOT NULL DEFAULT 0, ChangedAt TEXT NOT NULL, Reason TEXT NOT NULL DEFAULT '', " +
            "FOREIGN KEY (ProductId) REFERENCES Products(Id) ON DELETE CASCADE", cancellationToken);

        // Columns added over the lifetime of the app.
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "CategoryId", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "ModifierGroupId", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "IsAvailable", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "HasVariants", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "PhotoPath", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "Allergens", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "Tags", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "AvailableFromHour", "INTEGER NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Products", "AvailableToHour", "INTEGER NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "ModifierOptions", "IsAvailable", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "ReadyAt", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "CompletedAt", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "CancelledAt", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Orders", "CancellationReason", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "OrderItems", "SelectedVariantName", "TEXT NULL", cancellationToken);

        // Dead schema left behind by the removed VariantGroup/VariantOption models.
        await SqliteSchemaHelper.ExecuteAsync(db, "DROP TABLE IF EXISTS [VariantOptions]", cancellationToken);
        await SqliteSchemaHelper.ExecuteAsync(db, "DROP TABLE IF EXISTS [VariantGroups]", cancellationToken);
        await MigrationSql.DropColumnIfExistsAsync(db, "Products", "VariantGroupId", cancellationToken);
    }
}
