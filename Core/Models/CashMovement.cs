namespace CafePos.Core.Models;

/// <summary>What kind of drawer movement a records.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

public enum CashMovementKind
{
    /// <summary>Change put into the drawer. Raises the expected figure by the amount.</summary>
    Float = 1,

    /// <summary>Cash taken out of the drawer for collection. Lowers the expected figure.</summary>
    Payout = 2
}

/// <summary>One recorded movement of physical cash into or out of a shift's drawer.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

public class CashMovement
{
    public Guid Id { get; set; }

    public Guid ShiftId { get; set; }

    /// <summary>Which way the money went. Stored as TEXT (see the EF configuration) so the ledger stays readable when it is opened straight out of SQLite during an investigation.</summary>

    public CashMovementKind Kind { get; set; }

    /// <summary>Magnitude of the movement in kopecks. Always positive; see <see cref="SignedKopecks"/>.</summary>
    public long AmountKopecks { get; set; }

    /// <summary>Why the money moved.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public string? Reason { get; set; }

    /// <summary>When the movement was RECORDED (UTC), not when the money physically moved.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The movement this one cancels, or null when it is an ordinary movement.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public Guid? ReversesMovementId { get; set; }

    public Shift? Shift { get; set; }

    /// <summary>The amount as it affects the drawer: positive for a float, negative for a payout.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public long SignedKopecks => Kind == CashMovementKind.Float ? AmountKopecks : -AmountKopecks;
}
