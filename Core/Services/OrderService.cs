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

    public async Task<Shift> GetOrCreateActiveShiftAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var shift = await GetActiveShiftAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return shift;
    }

    /// <summary>
    /// Closes the active shift and opens a new one. Refuses to close a shift that still has
    /// orders in progress — previously a shift could be closed while orders were cooking.
    /// </summary>
    public async Task<Shift> CloseShiftAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var active = await db.Shifts.FirstOrDefaultAsync(shift => shift.IsActive, cancellationToken);

        var openOrders = await db.Orders.AsNoTracking()
            .CountAsync(order => order.ShiftId != null
                && (order.Status == OrderStatus.InProgress || order.Status == OrderStatus.Ready), cancellationToken);
        if (openOrders > 0)
        {
            throw new ConflictException($"Нельзя закрыть смену: осталось незакрытых заказов — {openOrders}.");
        }

        var now = timeProvider.GetUtcNow();
        if (active is not null)
        {
            active.IsActive = false;
            active.EndTime = now;
        }

        var next = new Shift
        {
            Id = Guid.NewGuid(),
            StartTime = now,
            IsActive = true,
            NextOrderNumber = 1
        };
        db.Shifts.Add(next);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Shift {PreviousShiftId} closed, new shift {ShiftId} opened", active?.Id, next.Id);
        return next;
    }

    private async Task<Shift> GetActiveShiftAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(current => current.IsActive, cancellationToken);
        if (shift is not null) return shift;

        shift = new Shift
        {
            Id = Guid.NewGuid(),
            StartTime = timeProvider.GetUtcNow(),
            IsActive = true,
            NextOrderNumber = 1
        };
        db.Shifts.Add(shift);
        return shift;
    }
}
