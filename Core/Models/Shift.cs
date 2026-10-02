namespace CafePos.Core.Models;

public class Shift
{
    public Guid Id { get; set; }

    /// <summary>Shift start (UTC).</summary>
    public DateTimeOffset StartTime { get; set; }

    public DateTimeOffset? EndTime { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Next order number for this shift. Incremented atomically in SQL
    /// (UPDATE ... RETURNING) so parallel checkouts can never take the same number.
    /// </summary>
    public int NextOrderNumber { get; set; } = 1;

    // ── Cash reconciliation ────────────────────────────────────────────────────────────────
    // Four columns, none of them derived and none of them re-writable once written. The difference
    // between the count and the expectation is NOT one of them: it is Counted − Expected, and a
    // stored copy would be a third copy of the same arithmetic.

    /// <summary>
    /// What was physically counted in the drawer at the close, in kopecks. NULL = never counted,
    /// which is a fact of its own and is what every shift that predates the feature holds; that is
    /// why the column is nullable rather than defaulting to 0. A count of <c>0</c> is a REAL count,
    /// not an absent one — an empty drawer is missing money, and reading it as "not counted" hides
    /// exactly the case worth catching.
    /// </summary>
    public long? CountedCashKopecks { get; set; }

    /// <summary>
    /// A FROZEN snapshot of the ledger's drawer figure at the moment of the close
    /// (<c>PaymentsCash − RefundsCash</c>), in kopecks. Frozen on purpose and written once: this is
    /// what the count was compared against, and it must keep saying that even after later refunds
    /// move the live figure — otherwise "the drawer was 120 short" silently becomes a different
    /// statement every time somebody takes money back out. Read the live figure from
    /// <c>ShiftStats.ExpectedCashNow</c>; <c>ShiftStats.IsReconciliationStale</c> is what reports that
    /// the two have parted company. NULL = never counted (see <see cref="CountedCashKopecks"/>).
    /// </summary>
    public long? ExpectedCashKopecks { get; set; }

    /// <summary>
    /// When the reconciliation was RECORDED, not when the cash was physically counted. It equals
    /// <see cref="EndTime"/> to the second, because the service records the close moment and the
    /// count is typed into the same flow — a small lie about the three minutes of counting that
    /// happened before it. It is kept anyway: it is the audit "when", and it is what makes a future
    /// recount cheap to add. Nothing derives anything from it.
    /// </summary>
    public DateTimeOffset? ReconciledAt { get; set; }

    /// <summary>
    /// Why the counted cash differed from the expectation, when it did. Mandatory on a mismatch and
    /// optional on a match. Never truncated — see <c>OrderService.CloseShiftAsync</c>.
    /// </summary>
    public string? CashDiscrepancyReason { get; set; }

    public List<Order> Orders { get; set; } = new();
}
