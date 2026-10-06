using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// Whether a shift is currently open, and the id of it.
/// </summary>
/// <remarks>
/// A CACHE, and that is the whole reason it exists rather than a direct query at every call site.
/// Navigation is synchronous — <c>Shell.OnNavigating</c> cannot await — so the answer has to already
/// be known when the question is asked. It is refreshed at startup, and after every operation that
/// can change the answer: opening, closing, and a restore from backup.
/// <para>
/// It holds NO state a reader should trust for money. <c>GetActiveShiftAsync</c> on
/// <see cref="IOrderService"/> is the authority for anything about a shift's contents; this answers
/// one question, "may the operator use the till at all", and it answers it from the database whenever
/// it is asked to re-read.
/// </para>
/// </remarks>
public interface IShiftSession
{
    /// <summary>True when a shift is open. Meaningless until <see cref="RefreshAsync"/> has run once.</summary>
    bool IsShiftOpen { get; }

    /// <summary>Id of the open shift, or null. Read from the cache for display; refresh before relying on it.</summary>
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
