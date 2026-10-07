using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>What is physically in one shift's drawer, and the four numbers it is made of.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

internal static class CashLedger
{
    public static async Task<Totals> ReadAsync(AppDbContext db, Guid shiftId, CancellationToken cancellationToken)
    {
        var payments = await ShiftPayments.ReadAsync(db, shiftId, cancellationToken).ConfigureAwait(false);

        /// <summary>Folded through the same rule CashMovement.SignedKopecks applies — see that property for why a correcting row's KIND, not its ReversesMovementId, decid…</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        var movements = await db.CashMovements.AsNoTracking()
            .Where(movement => movement.ShiftId == shiftId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var floatKopecks = movements
            .Where(movement => movement.Kind == CashMovementKind.Float)
            .Sum(movement => movement.AmountKopecks);
        var payoutKopecks = movements
            .Where(movement => movement.Kind == CashMovementKind.Payout)
            .Sum(movement => movement.AmountKopecks);

        return new Totals(payments, floatKopecks, payoutKopecks);
    }


    /// <summary>The whole cash picture of one shift: what came through the orders, and what was moved by hand.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    internal readonly record struct Totals(
        ShiftPayments.Totals Payments,
        long FloatKopecks,
        long PayoutKopecks)
    {
        /// <summary>What is physically in the drawer right now. May be negative; see the remarks above.</summary>
        public long InDrawerKopecks =>
            FloatKopecks + Payments.CashKopecks - Payments.RefundsCashKopecks - PayoutKopecks;

        /// <summary>Net effect of everything that was put in and taken out of the drawer by hand.</summary>
        public long MovementsKopecks => FloatKopecks - PayoutKopecks;
    }
}
