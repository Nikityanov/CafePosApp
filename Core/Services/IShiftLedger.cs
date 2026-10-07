using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>The shift, and the cash that is in the drawer because of it.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

public interface IShiftLedger
{
    /// <summary>Every shift on record, newest first.</summary>
    Task<List<Shift>> GetShiftsAsync(CancellationToken cancellationToken = default);

    /// <summary>The shift currently open, or null when none is.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a shift and records the change put into the drawer to start it.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>What the previous shift's count left in the drawer, for pre-filling the opening float.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task<long?> GetLastCountedCashKopecksAsync(CancellationToken cancellationToken = default);

    /// <summary>The shift a whole-archive export should describe: the open one, or the most recently closed when none is open.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task<Shift?> GetLatestShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the active shift, recording the counted cash against the drawer figure the ledger holds at that moment, and returns the shift that was closed. Fails when orders are still open.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default);
}
