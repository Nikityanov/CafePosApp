using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Money moving in or out of the drawer that did not come from an order.</summary>
public interface ICashLedgerService
{
    /// <summary>Puts change into the drawer of the active shift.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task RecordFloatAsync(long amountKopecks, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Takes cash out of the drawer of the active shift for collection.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    Task RecordPayoutAsync(long amountKopecks, string reason, CancellationToken cancellationToken = default);

    /// <summary>Cancels a movement already recorded on the ACTIVE shift, by adding the opposite one. Mandatory — it is the only statement of what was wrong.</summary>

    Task ReverseMovementAsync(Guid movementId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active shift's movements, oldest first, for the list on screen.
    /// </summary>
    Task<List<CashMovement>> GetMovementsAsync(Guid shiftId, CancellationToken cancellationToken = default);
}

/// <summary>The drawer movements an operator makes by hand: change in, cash out for collection, and the correcting entries that cancel either.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

public sealed class CashLedgerService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider) : ICashLedgerService
{
    /// <summary>Same bound as OrderPayment.Note and Shift.CashDiscrepancyReason.</summary>
    private const int MaxReasonLength = 300;

    public async Task RecordFloatAsync(long amountKopecks, string? reason, CancellationToken cancellationToken = default)
    {
        /// <summary>0 IS ALLOWED AND IS NOT A NO-OP.</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        if (amountKopecks < 0)
            throw new ValidationFailureException("Размен не может быть отрицательным.");

        var cleanReason = NormaliseReason(reason, required: false);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await RequireActiveShiftAsync(db, cancellationToken).ConfigureAwait(false);

        db.CashMovements.Add(NewMovement(shift, CashMovementKind.Float, amountKopecks, cleanReason));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordPayoutAsync(long amountKopecks, string reason, CancellationToken cancellationToken = default)
    {
        // 0 IS REFUSED, unlike a float, and the asymmetry is the point: putting no change in is a
        // statement about the till, while taking no money out is a button pressed by accident.
        if (amountKopecks <= 0)
            throw new ValidationFailureException("Сумма изъятия должна быть больше нуля.");

        var cleanReason = NormaliseReason(reason, required: true);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await RequireActiveShiftAsync(db, cancellationToken).ConfigureAwait(false);

        var ledger = await CashLedger.ReadAsync(db, shift.Id, cancellationToken).ConfigureAwait(false);

        /// <summary>The refusal names BOTH figures.</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        if (amountKopecks > ledger.InDrawerKopecks)
        {
            throw new ConflictException(
                $"Нельзя изъять {TextFormat.Money(Money.FromKopecks(amountKopecks))}: в кассе {TextFormat.Money(Money.FromKopecks(ledger.InDrawerKopecks))}. "
                + "Изъять можно не больше, чем записано в кассе.");
        }

        db.CashMovements.Add(NewMovement(shift, CashMovementKind.Payout, amountKopecks, cleanReason));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReverseMovementAsync(Guid movementId, string reason, CancellationToken cancellationToken = default)
    {
        var cleanReason = NormaliseReason(reason, required: true);
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var shift = await RequireActiveShiftAsync(db, cancellationToken).ConfigureAwait(false);

        var original = await db.CashMovements.AsNoTracking()
            .FirstOrDefaultAsync(movement => movement.Id == movementId, cancellationToken).ConfigureAwait(false)
            ?? throw new EntityNotFoundException("Движение денег не найдено.");

        /// <summary>Same shift, checked explicitly rather than by the absence of a shift filter: a correcting entry belongs to the drawer the mistake was made in, and can…</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        if (original.ShiftId != shift.Id)
            throw new ConflictException("Движение относится к другой смене.");

        if (original.ReversesMovementId is not null)
            throw new ConflictException("Это движение уже отменяет другое — отменить его нельзя.");

        // At most one correction per movement. Without this, two rows could each claim to undo the
        // same original and the drawer would be credited twice for one mistake.
        var alreadyReversed = await db.CashMovements.AsNoTracking()
            .AnyAsync(movement => movement.ReversesMovementId == movementId, cancellationToken).ConfigureAwait(false);
        if (alreadyReversed)
            throw new ConflictException("Это движение уже отменено.");

        /// <summary>The opposite kind, NOT the same kind with a link.</summary>
        /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

        var opposite = original.Kind == CashMovementKind.Float ? CashMovementKind.Payout : CashMovementKind.Float;

        var correction = NewMovement(shift, opposite, original.AmountKopecks, cleanReason);
        correction.ReversesMovementId = original.Id;
        db.CashMovements.Add(correction);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<CashMovement>> GetMovementsAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var movements = await db.CashMovements.AsNoTracking()
            .Where(movement => movement.ShiftId == shiftId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Oldest first, IN MEMORY: SQLite refuses ORDER BY on a DateTimeOffset outright rather than
        // sorting silently, and a shift carries tens of movements, not thousands.
        return movements.OrderBy(movement => movement.CreatedAt).ToList();
    }

    /// <summary>Builds a movement against `shift`, stamped from the app's clock so every row in this table carries the same `TimeProvider` the rest of the codebase uses and a test can move it.</summary>

    private CashMovement NewMovement(Shift shift, CashMovementKind kind, long amountKopecks, string? reason) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shift.Id,
        Kind = kind,
        AmountKopecks = amountKopecks,
        Reason = reason,
        CreatedAt = timeProvider.GetUtcNow()
    };

    /// <summary>The shift every operation here applies to, or a refusal the operator can act on.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    private static async Task<Shift> RequireActiveShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.IsActive, cancellationToken).ConfigureAwait(false);
        return shift ?? throw new ConflictException("Смена не открыта. Откройте смену, чтобы записывать движение денег.");
    }

    /// <summary>Trims a reason, refuses it when it is over-long, and insists on it when the kind needs one.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    private static string? NormaliseReason(string? reason, bool required)
    {
        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        if (required && trimmed is null)
            throw new ValidationFailureException("Укажите причину: она обязательна для этой операции.");

        if (trimmed is { Length: > MaxReasonLength })
        {
            throw new ValidationFailureException(
                $"Причина длиннее {MaxReasonLength} символов — она обрезается не молча, потому что это единственная запись о том, что произошло.");
        }

        return trimmed;
    }
}
