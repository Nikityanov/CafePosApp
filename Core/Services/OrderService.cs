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
    IContactDataService contacts,
    IComboService combos,
    ILogger<OrderService> logger) : IOrderService
{
    /// <summary>Matches the declared length of <c>Shift.CashDiscrepancyReason</c> (AppDbContext.Catalog).</summary>
    private const int MaxDiscrepancyReasonLength = 300;

    public async Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // ThenInclude, and not on purpose-later: OrderItem.Components is an un-Included collection that reads as EMPTY rather than stale — see the remarks on Or…
        // Почему так — `docs/decisions/orders.md`

        return await db.Orders
            .Include(order => order.Items)
                .ThenInclude(item => item.Components)
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
        /// <summary>Completed + Cancelled, i.e.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

    /// <summary>Opens a shift with the change recorded into its drawer in the same write.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

        /// <summary>The float is a CashMovement like any other, so the opening change is read by the same arithmetic as a mid-shift top-up and shows in the same list on s…</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

        /// <summary>Ordered IN MEMORY, and that is not laziness: SQLite cannot ORDER BY a DateTimeOffset, so this throws NotSupportedException rather than sorting silentl…</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

        /// <summary>An open shift wins outright, even if a closed one has a later EndTime — which happens the moment a shift is closed and the next is not yet opened, and in that window the closed one is exactly the report somebody wants.</summary>

        var active = await db.Shifts.AsNoTracking()
            .FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);
        if (active is not null) return active;

        // See GetLastCountedCashKopecksAsync: no DateTimeOffset ordering in SQLite.
        var closed = await db.Shifts.AsNoTracking()
            .Where(shift => !shift.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return closed.OrderByDescending(shift => shift.EndTime).FirstOrDefault();
    }

    /// <summary>Closes the active shift and opens a new one, recording the counted cash against the drawer figure the ledger holds at that moment.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task<Shift> CloseShiftAsync(long countedCashKopecks, string? discrepancyReason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var active = await db.Shifts.FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);

        /// <summary>Only the two money columns come back: SQLite cannot SUM an expression, so the open orders' totals are projected and the difference folded in memory.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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

        /// <summary>No active shift means there is nothing to reconcile the count against.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (active is null)
            throw new ConflictException("Нельзя закрыть смену: нет открытой смены.");

        /// <summary>The expectation, frozen by being read HERE and written to the row below.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var ledger = await CashLedger.ReadAsync(db, active.Id, cancellationToken);
        var expectedCashKopecks = ledger.InDrawerKopecks;

        /// <summary>0 is a REAL count and is stored as one.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (countedCashKopecks < 0)
            throw new ValidationFailureException("Пересчёт кассы не может быть отрицательным.");

        var discrepancyKopecks = countedCashKopecks - expectedCashKopecks;
        var reason = string.IsNullOrWhiteSpace(discrepancyReason) ? null : discrepancyReason.Trim();

        /// <summary>Required iff the count does not match, and never required when it does: demanding a reason for a drawer that came out exact would train the operator t…</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (discrepancyKopecks != 0 && reason is null)
            throw new ValidationFailureException(
                $"Пересчёт кассы не сходится с учётными данными: {TextFormat.Money(Money.FromKopecks(expectedCashKopecks))} в кассе, " +
                $"пересчитано {TextFormat.Money(Money.FromKopecks(countedCashKopecks))}. Укажите причину расхождения.");

        /// <summary>DELIBERATE INCONSISTENCY with PaymentRecorder.TruncateNote, which silently shortens a refund reason instead.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        if (reason is { Length: > MaxDiscrepancyReasonLength })
            throw new ValidationFailureException(
                $"Причина расхождения длиннее {MaxDiscrepancyReasonLength} символов — это поле аудита, сокращать его нельзя.");

        var now = timeProvider.GetUtcNow();
        active.CountedCashKopecks = countedCashKopecks;
        active.ExpectedCashKopecks = expectedCashKopecks;
        // "Recorded at", not "counted at": the operator counted the drawer a couple of minutes before this save, so the value equals EndTime to the second.
        // Почему так — `docs/decisions/orders.md`

        active.ReconciledAt = now;
        active.CashDiscrepancyReason = reason;
        active.IsActive = false;
        active.EndTime = now;

        /// <summary>NO NEXT SHIFT IS CREATED HERE, and that is the change this method exists to make.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        await db.SaveChangesAsync(cancellationToken);

        /// <summary>Retention runs here, AFTER the close has been committed, and that ordering is the decision.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var purgedPhones = await contacts.PurgeAsync(ContactDataService.DefaultRetention, cancellationToken);

        // The structured log is the other audit trail, and this codebase uses it deliberately: a
        // future migration can rewrite a column, it cannot rewrite a log line.
        logger.LogInformation(
            "Shift {PreviousShiftId} closed: counted {Counted} ₽ against {Expected} ₽ expected (float {Float} ₽, collected {Payout} ₿) ({Difference}), reason: {Reason}; contact purge cleared {PurgedPhones} phone numbers",
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
            reason ?? "—",
            purgedPhones);
        return active;
    }
}
