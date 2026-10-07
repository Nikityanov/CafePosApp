using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Whether a shift is currently open, and the id of it.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

public interface IShiftSession
{
    // True when a shift is open. Meaningless until <see cref="RefreshAsync"/> has run once.
    bool IsShiftOpen { get; }

    // Id of the open shift, or null. Read from the cache for display; refresh before relying on it.
    Guid? ActiveShiftId { get; }

    /// <summary>Re-reads the answer from the database.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a known state without a query, for when the caller just wrote it.</summary>
    void SetKnownState(bool isOpen, Guid? shiftId);
}

public sealed class ShiftSession(IDbContextFactory<AppDbContext> factory) : IShiftSession
{
    private bool isOpen;
    private Guid? shiftId;

    public bool IsShiftOpen => isOpen;

    public Guid? ActiveShiftId => shiftId;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await db.Shifts.AsNoTracking()
            .Where(row => row.IsActive)
            .Select(row => (Guid?)row.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        SetKnownState(shift is not null, shift);
    }

    public void SetKnownState(bool isOpen, Guid? shiftId)
    {
        this.isOpen = isOpen;
        this.shiftId = isOpen ? shiftId : null;
    }
}
