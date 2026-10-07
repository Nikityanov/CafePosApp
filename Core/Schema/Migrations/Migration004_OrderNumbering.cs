using CafePos.Core.Data;

namespace CafePos.Core.Schema.Migrations;

/// <summary>Version 4 — order numbers move from "MAX(OrderNumber) + 1" (race prone) to a per-shift counter that is incremented atomically in SQL, plus the unique index that guards it.</summary>

internal sealed class Migration004_OrderNumbering : ISchemaMigration
{
    public int Version => 4;
    public string Name => "Shift.NextOrderNumber + unique order number index";

    public async Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await MigrationSql.AddColumnIfMissingAsync(db, "Shifts", "NextOrderNumber", "INTEGER NOT NULL DEFAULT 1", cancellationToken);

        // Existing databases may contain duplicated numbers inside a shift; renumber them
        // in creation order so the unique index can be created.
        var duplicates = await SqliteSchemaHelper.ScalarLongAsync(db,
            "SELECT COUNT(*) FROM (SELECT ShiftId, OrderNumber FROM Orders GROUP BY ShiftId, OrderNumber HAVING COUNT(*) > 1)",
            cancellationToken).ConfigureAwait(false);

        if (duplicates > 0)
        {
            await SqliteSchemaHelper.ExecuteAsync(db,
                """
                UPDATE Orders SET OrderNumber = (
                    SELECT COUNT(*) FROM Orders AS earlier
                    WHERE earlier.ShiftId IS Orders.ShiftId
                      AND (earlier.CreatedAt < Orders.CreatedAt
                           OR (earlier.CreatedAt = Orders.CreatedAt AND earlier.Id <= Orders.Id)))
                """,
                cancellationToken).ConfigureAwait(false);
        }

        await SqliteSchemaHelper.ExecuteAsync(db,
            "CREATE UNIQUE INDEX IF NOT EXISTS [IX_Orders_ShiftId_OrderNumber] ON [Orders] ([ShiftId], [OrderNumber])",
            cancellationToken).ConfigureAwait(false);

        await SqliteSchemaHelper.ExecuteAsync(db,
            """
            UPDATE Shifts SET NextOrderNumber = COALESCE(
                (SELECT MAX(OrderNumber) + 1 FROM Orders WHERE Orders.ShiftId = Shifts.Id), 1)
            """,
            cancellationToken).ConfigureAwait(false);
    }
}
