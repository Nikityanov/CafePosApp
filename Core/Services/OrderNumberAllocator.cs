using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// Allocates the next per-shift order number with a single atomic SQL statement.
/// The previous implementation used MAX(OrderNumber) + 1, which throws a unique index
/// violation as soon as two checkouts happen at the same time.
/// </summary>
internal static class OrderNumberAllocator
{
    public static async Task<int> AllocateAsync(AppDbContext db, Shift shift, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = db.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "UPDATE [Shifts] SET [NextOrderNumber] = [NextOrderNumber] + 1 WHERE [Id] = $id RETURNING [NextOrderNumber] - 1";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = shift.Id.ToString();
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null and not DBNull) return Convert.ToInt32(result);
        }

        // Fallback for exotic builds where the bundled SQLite is older than 3.35 (no RETURNING).
        var next = await db.Orders.AsNoTracking()
            .Where(order => order.ShiftId == shift.Id)
            .MaxAsync(order => (int?)order.OrderNumber, cancellationToken).ConfigureAwait(false) ?? 0;
        shift.NextOrderNumber = next + 2;
        return next + 1;
    }

    public static async Task<int> PeekAsync(AppDbContext db, Guid shiftId, CancellationToken cancellationToken)
    {
        var value = await db.Shifts.AsNoTracking()
            .Where(shift => shift.Id == shiftId)
            .Select(shift => (int?)shift.NextOrderNumber)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return value ?? 1;
    }
}
