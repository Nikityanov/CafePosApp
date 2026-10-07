using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 8 — the end-of-shift cash count: `Shifts.CountedCashKopecks`, `Shifts.ExpectedCashKopecks`, `Shifts.ReconciledAt` and `Shifts.CashDiscrepancyR…</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal sealed class Migration008_CashReconciliation : ISchemaMigration
{
    public int Version => 8;
    public string Name => "Shift cash reconciliation";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(db, "Shifts", "CountedCashKopecks", "INTEGER NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Shifts", "ExpectedCashKopecks", "INTEGER NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Shifts", "ReconciledAt", "TEXT NULL", cancellationToken);
        await MigrationSql.AddColumnIfMissingAsync(db, "Shifts", "CashDiscrepancyReason", "TEXT NULL", cancellationToken);
    }
}
