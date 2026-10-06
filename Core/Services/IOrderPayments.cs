using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// Money moving against one order, in either direction, and the ledger of it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IOrderCommands"/> because the two fail differently. An order can advance
/// without money and money can move without the order advancing — a part-payment against an order
/// still cooking is the ordinary case, not an edge — so a port that folded them together would let
/// "record a payment" be written as if it implied the goods were handed over.
/// </remarks>
public interface IOrderPayments
{
    /// <summary>
    /// Records a payment against an order: adds the ledger row and moves Orders.PaidKopecks in one
    /// save. <paramref name="amount"/> is rubles, clamped to the outstanding balance (a tendered
    /// surplus is change, not revenue) and rejected when nothing is owed.
    /// </summary>
    Task<Order> AddPaymentAsync(Guid orderId, decimal amount, PaymentMethod method, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns money on a finished order: adds the refund row(s) and lowers Orders.PaidKopecks in one
    /// save. Only for <see cref="OrderStatus.Completed"/> — money goes back after the goods left the
    /// bar. <paramref name="amount"/> is rubles, clamped to what is still collected.
    /// <para>
    /// There is NO payment method parameter, and that is the point: the refund mirrors the order's
    /// own payments, oldest first. A POS has two tills, and booking the refund under a method the
    /// money never arrived in would leave one of them wrong at the count with nothing in the app to
    /// say so.
    /// </para>
    /// </summary>
    Task<Order> RefundAsync(Guid orderId, decimal amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>Payment ledger of one order, oldest first. Read through the DbSet, never through a collection.</summary>
    Task<List<OrderPayment>> GetOrderPaymentsAsync(Guid orderId, CancellationToken cancellationToken = default);
}