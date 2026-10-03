using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed partial class OrderService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<OrderService> logger) : IOrderService
{
    /// <summary>Matches the declared length of <c>Shift.CashDiscrepancyReason</c> (AppDbContext.Catalog).</summary>
    private const int MaxDiscrepancyReasonLength = 300;

    public async Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Orders
            .Include(order => order.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);
    }

    public async Task<List<Order>> GetActiveOrdersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.Status != OrderStatus.Completed && order.Status != OrderStatus.Cancelled)
            .ToListAsync(cancellationToken);
        // SQLite cannot ORDER BY a DateTimeOffset column in SQL — sort in memory.
        return orders.OrderBy(order => order.CreatedAt).ToList();
    }

    public async Task<List<Order>> GetCompletedOrdersAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var orders = await db.Orders.AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.ShiftId == shiftId && order.Status == OrderStatus.Completed)
            .ToListAsync(cancellationToken);
        return orders.OrderByDescending(order => order.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public async Task<List<Order>> GetShiftOrderHistoryAsync(Guid shiftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Completed + Cancelled, i.e. everything that is closed. The status is left on the entity
        // on purpose: the history list renders it, so a voided sale shows up AS voided instead of
        // being absent from the only list a manager reads after the fact.
        var orders = await db.Orders.AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.ShiftId == shiftId
                && (order.Status == OrderStatus.Completed || order.Status == OrderStatus.Cancelled))
            .ToListAsync(cancellationToken);
        return orders.OrderByDescending(order => order.CreatedAt).ToList();
    }

    public async Task<List<Shift>> GetShiftsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var shifts = await db.Shifts.AsNoTracking().ToListAsync(cancellationToken);
        return shifts
            .OrderByDescending(shift => shift.IsActive)
            .ThenByDescending(shift => shift.StartTime)
            .ToList();
    }

    public async Task<List<OrderStatusHistory>> GetStatusHistoryAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var history = await db.OrderStatusHistory.AsNoTracking()
            .Where(entry => entry.OrderId == orderId)
            .ToListAsync(cancellationToken);
        return history.OrderBy(entry => entry.ChangedAt).ToList();
    }

    public async Task<Shift?> GetActiveShiftAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);
    }

    /// <summary>
    /// Opens a shift with the change recorded into its drawer in the same write.
    /// </summary>
    /// <remarks>
    /// The float and the shift go in together, deliberately: a shift that exists before anyone has
    /// said what was in the drawer is a shift whose end-of-shift count has nothing to be compared
    /// against. That is precisely how this feature was broken before — the shift appeared by itself
    /// on first sale, so there was never a moment at which the change in the till was recorded
    /// against a shift that did not yet have orders in it.
    /// <para>
    /// The overlap guard runs first and before the float is validated, for the same reason the close
    /// checks its open orders first: the operator needs to know which part of the flow is wrong, and
    /// "a shift is already open" is a different fix from "that amount is impossible".
    /// </para>
    /// </remarks>
    public async Task<Shift> OpenShiftAsync(long floatKopecks, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (floatKopecks < 0)
            throw new ValidationFailureException("Размен не может быть отрицательным.");

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (cleanReason is { Length: > MaxDiscrepancyReasonLength })
            throw new ValidationFailureException(
                $"Причина длиннее {MaxDiscrepancyReasonLength} символов — это поле аудита, сокращать его нельзя.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if (await db.Shifts.AnyAsync(shift => shift.IsActive, cancellationToken))
        {
            throw new ConflictException("Смена уже открыта. Закройте её, прежде чем открывать новую.");
        }

        var now = timeProvider.GetUtcNow();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            StartTime = now,
            IsActive = true,
            NextOrderNumber = 1
        };
        db.Shifts.Add(shift);

        // The float is a CashMovement like any other, so the opening change is read by the same
        // arithmetic as a mid-shift top-up and shows in the same list on screen. It is NOT a column
        // on the shift: a column would make "the float" a different kind of fact from "change added
        // at 11:40", and the drawer is one sum of the same rows.
        db.CashMovements.Add(new CashMovement
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            Kind = CashMovementKind.Float,
            AmountKopecks = floatKopecks,
            Reason = cleanReason,
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Shift {ShiftId} opened at {StartedAt} with {Float} ₽ of change",
            shift.Id,
            now,
            Money.FromKopecks(floatKopecks));

        return shift;
    }

    public async Task<long?> GetLastCountedCashKopecksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Ordered IN MEMORY, and that is not laziness: SQLite cannot ORDER BY a DateTimeOffset, so
        // this throws NotSupportedException rather than sorting silently. A terminal has one shift
        // per working period, so the rows are a handful and loading two scalars each is cheaper than
        // any workaround that would keep the ordering in SQL.
        var counted = await db.Shifts.AsNoTracking()
            .Where(shift => !shift.IsActive && shift.CountedCashKopecks != null)
            .Select(shift => new { shift.EndTime, shift.CountedCashKopecks })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return counted
            .OrderByDescending(shift => shift.EndTime)
            .Select(shift => shift.CountedCashKopecks)
            .FirstOrDefault();
    }

    public async Task<Shift?> GetLatestShiftAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // An open shift wins outright, even if a closed one has a later EndTime — which happens the
        // moment a shift is closed and the next is not yet opened, and in that window the closed one
        // is exactly the report somebody wants.
        var active = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);
        if (active is not null) return active;

        // See GetLastCountedCashKopecksAsync: no DateTimeOffset ordering in SQLite.
        var closed = await db.Shifts.AsNoTracking()
            .Where(shift => !shift.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return closed.OrderByDescending(shift => shift.EndTime).FirstOrDefault();
    }

    /// <summary>
    /// Closes the active shift and opens a new one, recording the counted cash against the drawer
    /// figure the ledger holds at that moment. Refuses to close a shift that still has orders in
    /// progress — previously a shift could be closed while orders were cooking.
    /// <para>
    /// The refusal also names how much has not been collected, count and kopecks. A manager must
    /// not close a shift while 3000 ₽ of uncollected cash is still open on the bar, and a bare
    /// "3 orders left" gives no reason to go and take the money first.
    /// </para>
    /// <para>
    /// Reconciling is MANDATORY: the shift does not close without a count, and a difference has to
    /// carry a reason. A count of 0 is a real count (an empty drawer is missing money, not an absent
    /// count), and a count is never negative.
    /// </para>
    /// <para>
    /// THE FOUR COLUMNS ARE WRITTEN ONCE. There is no "исправить пересчёт" path and there must not
    /// be one: a count that can be silently overwritten is worse than no count, because the audit
    /// trail then says something the operator never stated. Recounting, if it is ever wanted,
    /// needs an append-only <c>CashCounts(ShiftId, CountedAt, CashKopecks, Reason)</c> table and a
    /// report that reads both — not an UPDATE over this row.
    /// </para>
    /// <para>
    /// What this does NOT freeze: a refund taken against an ALREADY CLOSED shift is allowed and
    /// moves that shift's LIVE drawer figure, because the money physically left the drawer that
    /// shift owned. The snapshot stays exactly where it was, and
    /// <see cref="ShiftStats.IsReconciliationStale"/> is the single boolean that reports that the
    /// two have parted company. There is no second money column, and the snapshot is never
    /// relabelled "expected" without the moment it was taken at.
    /// </para>
    /// </summary>
    public async Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var active = await db.Shifts.FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);

        // Only the two money columns come back: SQLite cannot SUM an expression, so the open
        // orders' totals are projected and the difference folded in memory. The set is a handful
        // of rows (everything else is closed), and a closed shift cannot have orders left open.
        //
        // DECISION, stated because it looks like an oversight otherwise: neither cancellation nor
        // refunding can block the close, and that is deliberate on both counts. A voided order is
        // Cancelled and a refunded one is Completed, so neither is in the InProgress/Ready set
        // below; a fully refunded Completed order has PaidKopecks == 0 and is exactly the order a
        // manager must be able to close out. Nothing here needs a status filter widened or a
        // "was it paid" test relaxed for the refund feature.
        //
        // The consequence, which is a decision rather than a bug: a refund against an ALREADY CLOSED
        // shift is allowed and retroactively changes that shift's report, because
        // GetShiftStatsAsync recomputes live off the ledger with no cache. That is correct — the
        // money physically left the drawer that the shift owned, so it belongs to that shift's
        // report whether the operator pressed the button during it or the next morning. Booking it
        // into the new shift instead would be the lie: the new shift's drawer never held the cash.
        //
        // ORDERING IS LOAD-BEARING: this guard runs BEFORE anything about the count or the reason.
        // The operator needs to know which part of the flow is wrong first — the bar still has orders
        // on it — and a close attempted with an empty drawer against a full expectation must report
        // the open orders, not the missing reason. Reordering the two would make an unrelated test
        // fail with the wrong exception type.
        var openOrders = await db.Orders.AsNoTracking()
            .Where(order => order.ShiftId != null
                && (order.Status == OrderStatus.InProgress || order.Status == OrderStatus.Ready))
            .Select(order => new { order.TotalKopecks, order.PaidKopecks })
            .ToListAsync(cancellationToken);
        if (openOrders.Count > 0)
        {
            var unpaid = openOrders.Where(order => order.PaidKopecks < order.TotalKopecks).ToList();
            var unpaidKopecks = unpaid.Sum(order => order.TotalKopecks - order.PaidKopecks);
            var unpaidSummary = unpaid.Count == 0
                ? string.Empty
                : $", из них не оплачено {unpaid.Count} на {TextFormat.Money(Money.FromKopecks(unpaidKopecks))}";

            throw new ConflictException($"Нельзя закрыть смену: осталось незакрытых заказов — {openOrders.Count}{unpaidSummary}.");
        }

        // No active shift means there is nothing to reconcile the count against. Refusing beats the
        // alternative this used to have — open a fresh shift and drop the count on the floor — since
        // the startup bootstrap and GetOrCreateActiveShiftAsync both guarantee an active shift, so
        // this is only reachable through a hand-edited database, and silently discarding a cash
        // count is the one outcome money must never have.
        //
        // This is reachable through the UI only by a terminal whose shift was closed and never
        // reopened; it used to be defended by an invariant that no longer exists, because startup no
        // longer creates a shift. The refusal stays: a count with nothing to reconcile against would
        // have to be discarded, and discarding a cash count is the one outcome money must never have.
        if (active is null)
            throw new ConflictException("Нельзя закрыть смену: нет открытой смены.");

        // The expectation, frozen by being read HERE and written to the row below.
        //
        // CashLedger, NOT ShiftPayments: the drawer is the float plus the cash from orders minus the
        // cash handed back minus what was carried away, and a count taken against the payments alone
        // would report a shortage of exactly the change that was in the till all day. Read once, in
        // this context, so the figure written is the figure that was compared against.
        //
        // It CAN be negative here, and that is not a bug to guard against: a cash refund taken after
        // a collection leaves money the drawer no longer holds (see CashLedger for the full case).
        // The count below cannot be negative, so such a shift closes as an overage with a reason
        // demanded, which is the correct thing to make a human look at.
        var ledger = await CashLedger.ReadAsync(db, active.Id, cancellationToken);
        var expectedCashKopecks = ledger.InDrawerKopecks;

        // 0 is a REAL count and is stored as one. An empty drawer against a non-zero expectation is
        // the single most important case this feature has to catch, so the guard is `>= 0` and
        // nothing else: a `?? 0`, a HasValue check or a `<= 0` here would swallow exactly that value
        // and turn missing money into an uncounted shift.
        if (countedCashKopecks < 0)
            throw new ValidationFailureException("Пересчёт кассы не может быть отрицательным.");

        var discrepancyKopecks = countedCashKopecks - expectedCashKopecks;
        var reason = string.IsNullOrWhiteSpace(discrepancyReason) ? null : discrepancyReason.Trim();

        // Required iff the count does not match, and never required when it does: demanding a reason
        // for a drawer that came out exact would train the operator to type filler into an audit
        // field. A reason on a match is accepted and stored.
        if (discrepancyKopecks != 0 && reason is null)
            throw new ValidationFailureException(
                $"Пересчёт кассы не сходится с учётными данными: {TextFormat.Money(Money.FromKopecks(expectedCashKopecks))} в кассе, " +
                $"пересчитано {TextFormat.Money(Money.FromKopecks(countedCashKopecks))}. Укажите причину расхождения.");

        // DELIBERATE INCONSISTENCY with PaymentRecorder.TruncateNote, which silently shortens a
        // refund reason instead. That one may be cut because the refunded money and the order it
        // belongs to are already recorded; this one may not, because this string is the ONLY record
        // of why the drawer did not balance, and DisplayPromptAsync has no MaxLength to stop a
        // paste. Rewriting it to 300 characters would alter what the operator stated and still look
        // like a complete answer. So: refuse, and leave the shift open.
        if (reason is { Length: > MaxDiscrepancyReasonLength })
            throw new ValidationFailureException(
                $"Причина расхождения длиннее {MaxDiscrepancyReasonLength} символов — это поле аудита, сокращать его нельзя.");

        var now = timeProvider.GetUtcNow();
        active.CountedCashKopecks = countedCashKopecks;
        active.ExpectedCashKopecks = expectedCashKopecks;
        // "Recorded at", not "counted at": the operator counted the drawer a couple of minutes before
        // this save, so the value equals EndTime to the second. Keeping the column is worth that
        // small lie — it is the audit moment, and it is what makes a recount cheap to add later —
        // but nothing derives anything from it (see Shift.ReconciledAt).
        active.ReconciledAt = now;
        active.CashDiscrepancyReason = reason;
        active.IsActive = false;
        active.EndTime = now;

        // NO NEXT SHIFT IS CREATED HERE, and that is the change this method exists to make. It used
        // to open one, because a shift had to exist before anybody could sell anything. Now a shift
        // is opened on purpose with the change recorded against it, so closing leaves the terminal
        // with NO open shift — which is the state the opening screen is for, and the state in which
        // the operator is asked to count the change they are about to put in.
        //
        // The old behaviour had a second, quieter cost: the new shift opened with a drawer figure of
        // zero while the physical drawer still held yesterday's change. Every report for the rest of
        // that shift understated it by exactly that amount.
        //
        // ONE save for the whole close: the four columns and the end of the shift are a single event,
        // and a half-applied close would leave a shift that is neither open nor reconciled.
        await db.SaveChangesAsync(cancellationToken);

        // The structured log is the other audit trail, and this codebase uses it deliberately: a
        // future migration can rewrite a column, it cannot rewrite a log line.
        logger.LogInformation(
            "Shift {PreviousShiftId} closed: counted {Counted} ₽ against {Expected} ₽ expected (float {Float} ₽, collected {Payout} ₿) ({Difference}), reason: {Reason}",
            active.Id,
            Money.FromKopecks(countedCashKopecks),
            Money.FromKopecks(expectedCashKopecks),
            Money.FromKopecks(ledger.FloatKopecks),
            Money.FromKopecks(ledger.PayoutKopecks),
            discrepancyKopecks switch
            {
                0 => "без расхождения",
                < 0 => $"не хватает {TextFormat.Money(Money.FromKopecks(-discrepancyKopecks))}",
                _ => $"излишек {TextFormat.Money(Money.FromKopecks(discrepancyKopecks))}"
            },
            reason ?? "—");
        return active;
    }
}
