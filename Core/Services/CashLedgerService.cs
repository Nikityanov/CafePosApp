using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Money moving in or out of the drawer that did not come from an order.</summary>
public interface ICashLedgerService
{
    /// <summary>
    /// Puts change into the drawer of the active shift.
    /// </summary>
    /// <param name="amountKopecks">Magnitude, kopecks. May be 0 and may never be negative.</param>
    /// <param name="reason">Optional — "put the change in" needs no elaboration.</param>
    Task RecordFloatAsync(long amountKopecks, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes cash out of the drawer of the active shift for collection.
    /// </summary>
    /// <param name="amountKopecks">Magnitude, kopecks. May never be 0 or negative.</param>
    /// <param name="reason">Mandatory. Money leaving the till has to account for itself.</param>
    Task RecordPayoutAsync(long amountKopecks, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a movement already recorded on the ACTIVE shift, by adding the opposite one.
    /// </summary>
    /// <param name="reason">Mandatory — it is the only statement of what was wrong.</param>
    Task ReverseMovementAsync(Guid movementId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active shift's movements, oldest first, for the list on screen.
    /// </summary>
    Task<List<CashMovement>> GetMovementsAsync(Guid shiftId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The drawer movements an operator makes by hand: change in, cash out for collection, and the
/// correcting entries that cancel either.
/// </summary>
/// <remarks>
/// Separate from <see cref="IOrderService"/> because none of this touches an order. It is also the
/// only place in the app where money moves with no customer behind it, which is exactly why the
/// rules are here and not spread across view models.
/// <para>
/// <b>THE ACTIVE SHIFT, ALWAYS.</b> No method takes a shift id. A movement recorded against the wrong
/// shift is the same class of mistake as reconciling one shift while closing another — the money
/// left the drawer of the shift that was open, and booking it anywhere else is a lie that a
/// reconciliation will later report as a shortage.
/// </para>
/// <para>
/// <b>WHY A PAYOUT IS CHECKED AGAINST THE DRAWER AND A REFUND IS NOT.</b> A payout is refused when it
/// exceeds the balance as recorded at that moment, because it is the one movement whose absence
/// would silently invent money: the drawer figure would rise by an outgoing movement nobody
/// recorded, and the count at the close would balance perfectly against a lie. A refund takes a
/// different path and is not checked — see <see cref="CashLedger"/> for why the balance can still
/// end up negative and why that is shown rather than prevented.
/// </para>
/// <para>
/// <b>WHY A CORRECTION IS ANOTHER ROW.</b> Editing the amount would leave one line saying what
/// happened, which is the outcome <c>CloseShiftAsync</c> refuses for the end-of-shift count: a
/// figure that can be silently overwritten stops being evidence. So a mistake produces a second row
/// pointing at the first, and the drawer carries both.
/// </para>
/// </remarks>
public sealed class CashLedgerService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider) : ICashLedgerService
{
    /// <summary>Same bound as OrderPayment.Note and Shift.CashDiscrepancyReason.</summary>
    private const int MaxReasonLength = 300;

    public async Task RecordFloatAsync(long amountKopecks, string? reason, CancellationToken cancellationToken = default)
    {
        // 0 IS ALLOWED AND IS NOT A NO-OP. A café whose change belongs to the owner opens with an
        // explicit 0 rather than with nothing: "I counted, there is nothing of mine in there" and
        // "nobody has opened the till yet" are different facts and the second one is the one worth
        // catching. Writing the row costs nothing and keeps every shift uniform.
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

        // The refusal names BOTH figures. "Insufficient funds" against a drawer the operator cannot
        // see is unactionable, and the whole value of checking here is that the operator is told what
        // the app believes is in there before they go and count it. TextFormat.Money and not
        // Money.FromKopecks: a raw decimal prints "100.01", which is not how any of this is written
        // anywhere a human reads it.
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

        // Same shift, checked explicitly rather than by the absence of a shift filter: a correcting
        // entry belongs to the drawer the mistake was made in, and cancelling a movement out of a
        // shift that is not open would put the two rows in different places for one mistake.
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

        // The opposite kind, NOT the same kind with a link. What physically happened is the point of
        // the row: cancelling a payout put money INTO the drawer, and recording that as a reversed
        // payout would say the opposite about which way it went while still netting correctly. A
        // reader summing the column by hand has to get the same answer the code does.
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

    /// <summary>
    /// Builds a movement against <paramref name="shift"/>, stamped from the app's clock so every row
    /// in this table carries the same <see cref="TimeProvider"/> the rest of the codebase uses and a
    /// test can move it.
    /// </summary>
    private CashMovement NewMovement(Shift shift, CashMovementKind kind, long amountKopecks, string? reason) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shift.Id,
        Kind = kind,
        AmountKopecks = amountKopecks,
        Reason = reason,
        CreatedAt = timeProvider.GetUtcNow()
    };

    /// <summary>
    /// The shift every operation here applies to, or a refusal the operator can act on.
    /// </summary>
    /// <remarks>
    /// The message names the fix, because "no shift" is the answer an operator gets for two quite
    /// different situations: a terminal that was never opened today, and one that was closed a
    /// moment ago. Both need "open the shift", so both get it said.
    /// </remarks>
    private static async Task<Shift> RequireActiveShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.IsActive, cancellationToken).ConfigureAwait(false);
        return shift ?? throw new ConflictException("Смена не открыта. Откройте смену, чтобы записывать движение денег.");
    }

    /// <summary>
    /// Trims a reason, refuses it when it is over-long, and insists on it when the kind needs one.
    /// </summary>
    /// <remarks>
    /// Truncation is refused for the reason <c>CloseShiftAsync</c> refuses it: the string is the
    /// record, and shortening what someone wrote still looks like a complete answer while being a
    /// different sentence. Deliberately inconsistent with <c>PaymentRecorder.TruncateNote</c>, which
    /// does shorten — there the money and the order it belongs to are recorded elsewhere, here
    /// nothing else preserves what was said.
    /// </remarks>
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
