using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 13 — Orders.SeenAt TEXT NULL: when the operator last looked at an order that had already become ready.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration013_OrderSeenAt : ISchemaMigration
{
    public int Version => 13;
    public string Name => "An order records when it was last seen after becoming ready";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(
            db, "Orders", "SeenAt", "TEXT NULL", cancellationToken);
    }
}
