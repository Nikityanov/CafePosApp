using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 8 — the end-of-shift cash count: <c>Shifts.CountedCashKopecks</c>,
/// <c>Shifts.ExpectedCashKopecks</c>, <c>Shifts.ReconciledAt</c> and
/// <c>Shifts.CashDiscrepancyReason</c>.
/// <para>
/// ALL FOUR ARE NULLABLE WITH NO DEFAULT AND NO BACKFILL, and that is the whole point. A default
/// would fabricate a count for every historical shift — the app would then claim that shifts nobody
/// ever counted had been counted at exactly 0 ₽, which is a false audit record on exactly the rows
/// an auditor looks at first. A NOT NULL column would do worse: SQLite cannot add one to a table
/// with rows in it. So every shift that predates this migration keeps NULL, which the domain reads
/// back as "never counted" — distinct from a real count of 0, which is missing money and the one
/// case the feature exists to catch.
/// </para>
/// <para>
/// There is deliberately no fifth column for the discrepancy. It is
/// <c>Counted − Expected</c>, and storing it would be a third copy of an arithmetic fact that
/// <c>CashReconciliation.DiscrepancyKopecks</c> already derives from the two that ARE stored.
/// </para>
/// <para>
/// Each statement is a bare ADD COLUMN with no DEFAULT, so it is O(1): SQLite never rewrites the
/// table for a nullable column, which matters on a device whose whole database lives in a phone's
/// flash.
/// </para>
/// </summary>
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
