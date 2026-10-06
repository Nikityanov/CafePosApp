using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// The shift, and the cash that is in the drawer because of it.
/// </summary>
/// <remarks>
/// <para>
/// The smallest of the five ports and the one that mattered most to carve out. Before the split,
/// <c>ShiftAnalyticsViewModel</c> — a screen that only reads — held an interface that offered
/// <c>CloseShiftAsync</c>, and <c>BackupService</c> held one that offered <c>RefundAsync</c>. Nothing
/// stopped either. Now the closing screen needs this port, and this port is the only place a shift
/// can be closed, so the two facts cannot drift apart.
/// </para>
/// <para>
/// The cashier-facing rules — the opening float is recorded, the close reconciles, no next shift is
/// opened — are documented on the methods themselves rather than restated here.
/// </para>
/// </remarks>
public interface IShiftLedger
{
    /// <summary>Every shift on record, newest first.</summary>
    Task<List<Shift>> GetShiftsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift currently open, or <c>null</c> when none is.
    /// </summary>
    /// <remarks>
    /// Null is a NORMAL state, not an error: a shift is opened deliberately now
    /// (<see cref="OpenShiftAsync"/>), so a terminal that has not been opened today, or was closed a
    /// moment ago, reports it as absent. It used to be an error — this call created a shift instead —
    /// which meant the opening float could never be recorded: the shift already existed by the time
    /// anybody was asked about the change in the drawer.
    /// </remarks>
    Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a shift and records the change put into the drawer to start it.
    /// </summary>
    /// <param name="floatKopecks">Change in the drawer at the opening, kopecks. May be 0 and may never
    /// be negative: a café whose change belongs to the owner opens with an explicit zero, which is a
    /// different and more useful fact than an absent float.</param>
    /// <param name="reason">Optional note on the float; see <see cref="ICashLedgerService.RecordFloatAsync"/>.</param>
    /// <remarks>
    /// REFUSES when a shift is already open. Two overlapping shifts would each have their own float,
    /// their own orders and their own count, and the drawer they describe would be the sum of two
    /// tills — the reconciliation would then balance against a figure nobody could have counted.
    /// <para>
    /// There is no float parameter that must be positive and no way to open a shift without saying
    /// what is in the drawer, because "I opened the till and counted it" is the one statement the
    /// end-of-shift count is later compared against.
    /// </para>
    /// </remarks>
    Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the previous shift's count left in the drawer, for pre-filling the opening float.
    /// </summary>
    /// <remarks>
    /// The COUNTED figure, not the expected one, because what carries over is what was physically
    /// there. Null when there is no earlier shift, or when the last one was closed without ever being
    /// counted — in which case there is nothing to suggest and the operator types it.
    /// </remarks>
    Task<long?> GetLastCountedCashKopecksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The shift a whole-archive export should describe: the open one, or the most recently closed
    /// when none is open.
    /// </summary>
    /// <remarks>
    /// Null only on a terminal that has never had a shift, which is a demo seed rather than a real
    /// state. Exists so the backup does not reach into the DbContext to decide which shift a report
    /// is about — that choice is a shift question and belongs with the shift rules.
    /// </remarks>
    Task<Shift?> GetLatestShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the active shift, recording the counted cash against the drawer figure the ledger holds
    /// at that moment, and returns the shift that was closed. Fails when orders are still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO NEXT SHIFT IS OPENED. It used to be, because a shift had to exist before anything could be
    /// sold; a shift is now opened on purpose with its change recorded, so closing leaves the terminal
    /// with no open shift and the opening screen is what comes next.
    /// </para>
    /// <para>
    /// Reconciling is MANDATORY: a shift cannot be closed without entering what was counted, and a
    /// mismatch has to carry a reason. <paramref name="countedCashKopecks"/> is kopecks, may be 0
    /// (an empty drawer is the case that matters most, not an absent count) and may never be
    /// negative. <paramref name="discrepancyReason"/> is required iff the count differs from the
    /// expectation, accepted but never required when it matches, and rejected rather than truncated
    /// when it is over-long — it is a mandatory audit field.
    /// </para>
    /// <para>
    /// The expectation is <c>CashLedger</c>'s figure: the opening float, plus the cash from orders,
    /// less the cash handed back and less what was carried away. A count taken against the payments
    /// alone would report a shortage of exactly the change that was in the till all day.
    /// </para>
    /// <para>
    /// There is NO shiftId parameter, deliberately: the service resolves the active shift itself.
    /// A caller naming it could reconcile one shift and have the service close another.
    /// </para>
    /// <para>
    /// A count, once recorded, is NOT re-editable — there is no path that rewrites these four
    /// columns. A silently overwritten count is worse than no count. Recounting, if it is ever
    /// wanted, needs an append-only <c>CashCounts(ShiftId, CountedAt, CashKopecks, Reason)</c>
    /// table and a report that reads both.
    /// </para>
    /// </remarks>
    Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default);
}
