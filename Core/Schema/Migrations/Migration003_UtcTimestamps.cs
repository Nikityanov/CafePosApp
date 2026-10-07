using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 3 — every timestamp is stored in UTC with an explicit "+00:00" offset.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration003_UtcTimestamps : ISchemaMigration
{
    public int Version => 3;
    public string Name => "Timestamps -> UTC with offset";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.ConvertToUtcAsync(db, "Orders", "CreatedAt", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "Orders", "ReadyAt", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "Orders", "CompletedAt", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "Orders", "CancelledAt", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "Shifts", "StartTime", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "Shifts", "EndTime", cancellationToken);
        await MigrationSql.ConvertToUtcAsync(db, "PriceHistoryEntries", "ChangedAt", cancellationToken);
    }
}
