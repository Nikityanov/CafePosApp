using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Taking payments after the fact: the payment sheet of an open order, and the ledger readout. Both delegate to , the single place where a payment row and Orders.PaidKopecks are written together.</summary>

public sealed partial class OrderService
{
    public async Task<Order> AddPaymentAsync(Guid orderId, decimal amount, PaymentMethod method, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Tracked on purpose: the scalar moved by the recorder has to be picked up as an UPDATE.
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        PaymentRecorder.Record(db, order, amount, method, timeProvider.GetUtcNow(), logger);

        await db.SaveChangesAsync(cancellationToken);
        return order;
    }

    public async Task<List<OrderPayment>> GetOrderPaymentsAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var payments = await db.OrderPayments.AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .ToListAsync(cancellationToken);

        // SQLite cannot ORDER BY a DateTimeOffset column in SQL (the same reason the order lists
        // sort in memory), and the ledger of one order is small enough for that to be free.
        return payments.OrderBy(payment => payment.PaidAt).ToList();
    }
}
