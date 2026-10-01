using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 7 — refunds in the payment ledger: <c>OrderPayments.IsRefund</c> and
/// <c>OrderPayments.Note</c>.
/// <para>
/// Both columns are additions with a literal default, so each statement is O(1): SQLite's
/// ADD COLUMN never rewrites the table when the new column is constant-defaulted, which matters on
/// a device whose whole database lives in a phone's flash.
/// </para>
/// <para>
/// WHY there is no backfill, unlike version 6's PaidKopecks: every row that exists is a collection,
/// so <c>IsRefund = 0</c> is already the correct value and a second UPDATE pass would be pure
/// cost. Version 6 needed one because a NOT NULL column with no default would have turned every
/// pre-existing open order into a debtor; here the default IS the truth. (A converter would have
/// been the other way to be safe — see the EF model, which deliberately has none, following
/// <c>DraftOrder.IsActiveCart</c>.)
/// </para>
/// <para>
/// <c>Note</c> is nullable TEXT: a payment has no reason to give, and a refund always has one.
/// </para>
/// </summary>
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
