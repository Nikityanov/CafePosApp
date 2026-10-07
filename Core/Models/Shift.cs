namespace CafePos.Core.Models;

public class Shift
{
    public Guid Id { get; set; }

    /// <summary>Shift start (UTC).</summary>
    public DateTimeOffset StartTime { get; set; }

    public DateTimeOffset? EndTime { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Next order number for this shift. Incremented atomically in SQL (UPDATE ... RETURNING) so parallel checkouts can never take the same number.</summary>

    public int NextOrderNumber { get; set; } = 1;

    /// <summary>── Cash reconciliation ──────────────────────────────────────────────────────────────── Four columns, none of them derived and none of them re-writabl…</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>


    /// <summary>What was physically counted in the drawer at the close, in kopecks.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public long? CountedCashKopecks { get; set; }

    /// <summary>A FROZEN snapshot of the ledger's drawer figure at the moment of the close (`PaymentsCash − RefundsCash`), in kopecks.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public long? ExpectedCashKopecks { get; set; }

    /// <summary>When the reconciliation was RECORDED, not when the cash was physically counted.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public DateTimeOffset? ReconciledAt { get; set; }

    /// <summary>Why the counted cash differed from the expectation, when it did. Mandatory on a mismatch and optional on a match. Never truncated — see OrderService.CloseShiftAsync.</summary>

    public string? CashDiscrepancyReason { get; set; }

    public List<Order> Orders { get; set; } = new();

    /// <summary>Cash into and out of this shift's drawer: the opening float, any top-up, any collection, and any correcting entry that cancels one of them.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public List<CashMovement> CashMovements { get; set; } = new();
}
