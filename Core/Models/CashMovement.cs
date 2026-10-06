namespace CafePos.Core.Models;

/// <summary>
/// What kind of drawer movement a <see cref="CashMovement"/> records.
/// </summary>
/// <remarks>
/// Two kinds and no third. A <c>Float</c> is change going IN — the opening float or a top-up during
/// the shift — and a <c>Payout</c> is cash taken OUT for collection. Both exist because the drawer
/// is not only sales: a till that has no change cannot break a 1000 ₽ note, and that is not a
/// bookkeeping detail, it is the till refusing to do its job.
/// <para>
/// There is deliberately no <c>Correction</c> member. A wrong amount is not a third kind of thing
/// that happened to the drawer, it is the same thing recorded wrongly, and the correction is the
/// same movement pointing back at the original through <see cref="CashMovement.ReversesMovementId"/>.
/// A <c>Correction</c> member would let a row exist with no movement to correct, which is a hole in
/// the ledger rather than an entry in it.
/// </para>
/// </remarks>
public enum CashMovementKind
{
    /// <summary>Change put into the drawer. Raises the expected figure by the amount.</summary>
    Float = 1,

    /// <summary>Cash taken out of the drawer for collection. Lowers the expected figure.</summary>
    Payout = 2
}

/// <summary>
/// One recorded movement of physical cash into or out of a shift's drawer.
/// </summary>
/// <remarks>
/// APPEND-ONLY. No row here is ever updated or deleted, which is the same rule
/// <c>OrderService.CloseShiftAsync</c> applies to the end-of-shift count and for the same reason: a
/// figure that can be silently overwritten stops being evidence. A mistake is answered with another
/// row, not with an edit.
/// <para>
/// <b>THE AMOUNT IS ALWAYS POSITIVE.</b> Direction is <see cref="Kind"/>, flipped by
/// <see cref="ReversesMovementId"/> — never a negative <see cref="AmountKopecks"/>. A signed column
/// would put the same fact in two places: the number says how much and its sign says which way,
/// and any code that forgets the second reads a payout as money arriving. Keeping the amount
/// unsigned means <see cref="SignedKopecks"/> is the ONLY place direction is decided, so there is
/// exactly one definition to keep in step.
/// </para>
/// <para>
/// A <see cref="Payout"/> is refused when it exceeds what the drawer holds at that moment, so this
/// table alone never drives the balance negative. The balance can STILL go negative another way: a
/// cash refund taken after a payout leaves money the drawer no longer has. That is allowed on
/// purpose — the refund's own rules are the carefully tested part of this app, and physically the
/// money comes from a reserve outside the drawer — and the negative balance is shown rather than
/// hidden. See <c>CashLedger</c>.
/// </para>
/// </remarks>
public class CashMovement
{
    public Guid Id { get; set; }

    public Guid ShiftId { get; set; }

    /// <summary>
    /// Which way the money went. Stored as TEXT (see the EF configuration) so the ledger stays
    /// readable when it is opened straight out of SQLite during an investigation.
    /// </summary>
    public CashMovementKind Kind { get; set; }

    /// <summary>Magnitude of the movement in kopecks. Always positive; see <see cref="SignedKopecks"/>.</summary>
    public long AmountKopecks { get; set; }

    /// <summary>
    /// Why the money moved. Mandatory on a <see cref="CashMovementKind.Payout"/> — money leaving the
    /// till needs an account of itself — and optional on a <see cref="CashMovementKind.Float"/>,
    /// because "put the change in" needs no elaboration. Never truncated: an over-long reason is
    /// refused, the same rule <c>CloseShiftAsync</c> applies to a discrepancy reason, for the same
    /// reason.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>When the movement was RECORDED (UTC), not when the money physically moved.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The movement this one cancels, or <c>null</c> when it is an ordinary movement.
    /// </summary>
    /// <remarks>
    /// Self-referencing rather than a <c>CashMovementKind.Correction</c> member, so the link is a
    /// fact in the data and not a convention a reader has to know. The original row stays exactly as
    /// it was written, which is the entire point: the drawer has two rows saying what happened, one
    /// saying what was wrong and one undoing it.
    /// <para>
    /// A movement can be cancelled at most once, and only while its shift is still open. Both rules
    /// are enforced by <c>CashLedgerService</c> rather than by the database, because SQLite has
    /// nothing to say about them and a constraint that fires on a hand-edited database is worse than
    /// a check that reports what is actually there.
    /// </para>
    /// </remarks>
    public Guid? ReversesMovementId { get; set; }

    public Shift? Shift { get; set; }

    /// <summary>
    /// The amount as it affects the drawer: positive for a float, negative for a payout.
    /// </summary>
    /// <remarks>
    /// The single definition of direction. Summing this column over a shift's movements, and adding
    /// it to the cash payments, is the whole of "what is in the drawer".
    /// <para>
    /// <see cref="ReversesMovementId"/> does NOT enter this arithmetic, and that is worth stating
    /// because the obvious first implementation negates on it and is wrong twice over. A correcting
    /// row carries the OPPOSITE kind from the movement it undoes — cancelling a collection puts money
    /// back, so it is a <see cref="CashMovementKind.Float"/> — which means the kind already says
    /// which way it went. Negating again would turn a 500 ₿ collection that was corrected back into a
    /// 500 ₿ deposit, and the drawer would drift by exactly the mistakes somebody bothered to correct.
    /// The link is a fact about the ledger's history; the sign is a fact about the cash.
    /// </para>
    /// </remarks>
    public long SignedKopecks => Kind == CashMovementKind.Float ? AmountKopecks : -AmountKopecks;
}
