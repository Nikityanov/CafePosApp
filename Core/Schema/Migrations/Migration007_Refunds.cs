using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 7 — refunds in the payment ledger: `OrderPayments.IsRefund` and `OrderPayments.Note`.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration007_Refunds : ISchemaMigration
{
    public int Version => 7;
    public string Name => "Order payment refunds";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(db, "OrderPayments", "IsRefund", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "OrderPayments", "Note", "TEXT NULL", cancellationToken);
    }
}
