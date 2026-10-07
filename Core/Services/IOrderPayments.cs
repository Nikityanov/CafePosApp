using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>Money moving against one order, in either direction, and the ledger of it.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IOrderPayments
{
    /// <summary>Records a payment against an order: adds the ledger row and moves Orders.PaidKopecks in one save.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<Order> AddPaymentAsync(Guid orderId, decimal amount, PaymentMethod method, CancellationToken cancellationToken = default);

    /// <summary>Returns money on a finished order: adds the refund row(s) and lowers Orders.PaidKopecks in one save.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>Payment ledger of one order, oldest first. Read through the DbSet, never through a collection.</summary>
    Task<List<OrderPayment>> GetOrderPaymentsAsync(Guid orderId, CancellationToken cancellationToken = default);
}
