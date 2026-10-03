using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>
/// Version 9 — <c>CashMovements</c>: the change put into the drawer and the cash taken out of it,
/// apart from anything that arrived through an order.
/// </summary>
/// <para>
/// <b>AN EMPTY TABLE, AND NO BACKFILL — WHICH IS THE ONLY HONEST ANSWER.</b> The three columns that
/// would carry a history (shift, kind, amount) cannot be reconstructed for a shift that has already
/// closed: the opening float is not derivable from the orders (that is the whole gap this table
/// fills), and an incassation leaves no trace anywhere else. Any value written here would be a
/// figure nobody ever stated, sitting in the one table an auditor reads expecting exactly that. An
/// empty table reads as "this terminal did not record drawer movements before version 9", which is
/// true.
/// </para>
/// <para>
/// The consequence is stated so it is not rediscovered as a bug: a shift closed BEFORE this
/// migration reconciles against payments only, exactly as it always did. A shift opened after it
/// includes the float in what the drawer is expected to hold, so the two kinds of shift in the same
/// report differ by whether the operator recorded the change — not by any migration artefact.
/// </para>
/// <para>
/// <c>AmountKopecks</c> is NOT NULL with no default and holds a magnitude only; direction lives in
/// <c>Kind</c> and in <c>ReversesMovementId</c>. <c>Kind</c> is TEXT, matching how
/// <c>OrderPayment.Method</c> is stored, so the ledger is legible when opened in SQLite rather than
/// being ordinals only this build understands.
/// </para>
/// <para>
/// <c>ReversesMovementId</c> gets no FOREIGN KEY, deliberately. It points at a row in this same
/// table, and the one thing the app never does is delete a movement — so a constraint here would
/// only ever fire on a hand-edited database, where a cascade would take a second row with it and a
/// plain reference would turn a stray id into a write failure with no explanation. The link is
/// checked in <c>CashLedgerService</c>, which can say what is wrong with it.
/// </para>
/// <para>
/// The index is created explicitly because <see cref="SqliteSchemaHelper"/> parity is enforced by a
/// test that reads columns, not indexes: without this statement an upgraded terminal would have the
/// table with no index while a fresh one got one from EnsureCreated, and that divergence is
/// invisible until the day the drawer is large enough to notice.
/// </para>
/// </remarks>
internal sealed class Migration009_CashMovements : ISchemaMigration
{
    public int Version => 9;
    public string Name => "Cash movements in and out of the drawer";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.CreateTableAsync(db, "CashMovements",
            "Id TEXT NOT NULL PRIMARY KEY, " +
            "ShiftId TEXT NOT NULL, " +
            "Kind TEXT NOT NULL, " +
            "AmountKopecks INTEGER NOT NULL, " +
            "Reason TEXT NULL, " +
            "CreatedAt TEXT NOT NULL, " +
            "ReversesMovementId TEXT NULL, " +
            "FOREIGN KEY (ShiftId) REFERENCES Shifts(Id) ON DELETE CASCADE",
            cancellationToken);

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE INDEX IF NOT EXISTS [IX_CashMovements_ShiftId] ON [CashMovements] ([ShiftId])",
            cancellationToken);
    }
}