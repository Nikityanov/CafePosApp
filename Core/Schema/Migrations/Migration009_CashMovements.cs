using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 9 — `CashMovements`: the change put into the drawer and the cash taken out of it, apart from anything that arrived through an order.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
