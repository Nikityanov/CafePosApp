using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// What is physically in one shift's drawer, and the four numbers it is made of.
/// </summary>
/// <remarks>
/// THE ONE DEFINITION. <see cref="ShiftPayments"/> used to carry it as
/// <c>Totals.CashInDrawerKopecks</c> — "cash taken, less cash given back" — and that is the whole
/// reason this class exists. It is true only for a till opened empty and never emptied: a café that
/// puts 500 ₿ of change in at 08:00 and bags 3000 ₿ at 15:00 has a drawer that is neither, and
/// against it the end-of-shift count would report a shortage of exactly the change.
/// <para>
/// So the drawer figure is defined HERE and only here, as four added facts rather than two:
/// the float that went in, the cash that arrived through orders, the cash that went back out
/// through refunds, and the cash that was carried away.
/// </para>
/// <para>
/// <b>THE NON-NEGATIVITY THAT IS NOT A GUARANTEE ANY MORE, STATED HONESTLY.</b>
/// <see cref="ShiftPayments"/> argued the drawer can never go below zero, and the argument was
/// sound — it rested on refunds never exceeding collections, which
/// <c>PaymentRecorder.AllocateMirroredSlices</c> guarantees per order and which therefore sums to
/// the same inequality over a shift. Movements break the argument in two different ways, and both
/// were considered:
/// <list type="number">
/// <item>A payout larger than the drawer. Refused outright, in <c>CashLedgerService</c>, against the
/// balance as it stands at that moment. This is the case a check can actually prevent.</item>
/// <item>A cash refund taken AFTER that payout. Not preventable and not prevented: the refund's own
/// rules are the carefully tested part of this codebase, and physically the money comes from a
/// reserve outside the drawer, so a negative balance is a fact about the world rather than a bug.
/// 0 ₿ float, 1000 ₿ of cash sales, 1000 ₿ carried away, then a 1000 ₿ cash refund, leaves −1000 ₿,
/// and every one of those four steps is legitimate.</item>
/// </list>
/// So the rule the code can hold to is the precise one: a movement that takes money out may not
/// exceed the drawer as recorded up to that moment. The balance afterwards may still be negative,
/// and it is shown rather than hidden, because a hidden negative is indistinguishable from a bug in
/// the arithmetic.
/// </para>
/// </remarks>
internal static class CashLedger
{
    public static async Task<Totals> ReadAsync(AppDbContext db, Guid shiftId, CancellationToken cancellationToken)
    {
        var payments = await ShiftPayments.ReadAsync(db, shiftId, cancellationToken).ConfigureAwait(false);

        // Folded through the same rule CashMovement.SignedKopecks applies — see that property for why
        // a correcting row's KIND, not its ReversesMovementId, decides the direction. The rows are
        // materialised rather than summed in SQL: an expression like "Kind == Float ? Amount : -Amount"
        // transliterates happily, works today, and is a second copy of a rule that also lives on the
        // entity. A shift holds as many movements as a busy day has floats and collections: tens of rows.
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


    /// <summary>
    /// The whole cash picture of one shift: what came through the orders, and what was moved by hand.
    /// </summary>
    /// <remarks>
    /// The payments aggregates are carried here rather than read separately by the report so that
    /// "one read, one definition" holds. <see cref="ShiftPayments.Totals"/> is reachable through
    /// <see cref="Payments"/>, and it deliberately exposes no drawer figure of its own — see that
    /// type for why it no longer has one.
    /// </remarks>
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
