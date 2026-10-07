using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 2 — money becomes INTEGER kopecks. Previously decimals were stored as TEXT: SUM(decimal) is not supported by SQLite at all and ORDER BY/comparisons on money columns compared strings ("1000" sorts before "250").</summary>

internal sealed class Migration002_MoneyToKopecks : ISchemaMigration
{
    public int Version => 2;
    public string Name => "Money columns TEXT -> INTEGER kopecks";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.ConvertMoneyColumnAsync(db, "Products", "Price", "PriceKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "ProductVariants", "Price", "PriceKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "OrderItems", "Price", "PriceKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "Orders", "TotalPrice", "TotalKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "PriceRules", "PriceOverride", "PriceOverrideKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "PriceRules", "PriceAdjustment", "PriceAdjustmentKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "PriceHistoryEntries", "OldPrice", "OldPriceKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "PriceHistoryEntries", "NewPrice", "NewPriceKopecks", cancellationToken);
        await MigrationSql.ConvertMoneyColumnAsync(db, "Ingredients", "CostPerUnit", "CostPerUnitKopecks", cancellationToken);

        // Legacy databases may have written prices by hand; make sure the values are consistent.
        await SqliteSchemaHelper.ExecuteAsync(db, "UPDATE [Orders] SET [TotalKopecks] = COALESCE((SELECT SUM([PriceKopecks] * [Quantity]) FROM [OrderItems] WHERE [OrderItems].[OrderId] = [Orders].[Id]), 0)", cancellationToken);
    }
}
