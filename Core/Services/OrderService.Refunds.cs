using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Taking money back after the fact.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public sealed partial class OrderService
{
    /// <summary>Refunds part or all of a finished order.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public async Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Tracked on purpose: the scalar moved by the recorder has to be picked up as an UPDATE.
        var order = await db.Orders.FirstOrDefaultAsync(current => current.Id == orderId, cancellationToken)
            ?? throw new EntityNotFoundException("Заказ не найден.");

        /// <summary>Restricting refunds to Completed is load-bearing, not a UX preference: it is what keeps AdvanceStatusAsync's IsFullyPaid guard sound.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        PaymentRecorder.Refund(db, order, Money.ToKopecks(amount), reason, timeProvider.GetUtcNow(), logger);

        await db.SaveChangesAsync(cancellationToken);
        return order;
    }
}
