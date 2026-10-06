using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// Changing an order: its status, its lines, its contact details.
/// </summary>
/// <remarks>
/// <para>
/// Money is NOT here. <see cref="IOrderPayments"/> owns the ledger, because "the order moved on" and
/// "the till moved" are separate decisions with separate reasons to be wrong: a status can advance
/// without money (kitchen progress), and money can move without the status changing (a part-payment
/// against an order still cooking).
/// </para>
/// <para>
/// Every method here is a command — it writes, it may refuse, and it is not idempotent.
/// <see cref="IOrderQueries"/> is the read side.
/// </para>
/// </remarks>
public interface IOrderCommands
{
    /// <summary>Moves the order to the next status and returns the updated order.</summary>
    Task<Order> AdvanceStatusAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Voids a sale. A paid order is NOT refused: the money is refunded in full, mirrored back out
    /// of the methods it arrived in, in the same transaction that flips the status — the two guards
    /// this used to have ("the order is closed" and "the order is paid") are gone, because both of
    /// them left a real cashier with no way to give a customer their money back.
    /// <paramref name="stock"/> is the operator's one decision: leave the ingredients written off
    /// (made, handed over, or a discrepancy) or return them to the shelf.
    /// </summary>
    Task CancelOrderAsync(
        Guid orderId,
        string? reason = null,
        StockDisposition stock = StockDisposition.LeaveWrittenOff,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the item list of an editable order (differential update, ids are kept).</summary>
    Task UpdateOrderAsync(Guid orderId, IReadOnlyCollection<OrderItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that somebody has opened this order, retiring the board's unread dot for it.
    /// </summary>
    /// <remarks>
    /// Writes <see cref="Order.SeenAt"/> and nothing else. It is deliberately not coupled to
    /// <c>UpdateOrderAsync</c> or to the status transition: the dot exists to answer "has anyone looked
    /// since it became ready", and the answer must come from somebody actually opening the order, not
    /// from whatever else happened to touch the row.
    /// <para>
    /// MONOTONIC, and that matters because the board auto-refreshes. A plain assignment would move
    /// <c>SeenAt</c> backwards every time the operator reopened an order that had already been seen,
    /// and a read older than <see cref="Order.ReadyAt"/> would bring the dot back for an order nobody
    /// has touched since. <c>max</c> keeps the newest look.
    /// </para>
    /// <para>
    /// Silent when the order is missing or already fully seen: this is a courtesy write on a path the
    /// operator did not choose to enter deliberately, and a screen that failed because a dot was
    /// already gone would be a worse board than the dot.
    /// </para>
    /// </remarks>
    Task MarkSeenAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a phone and a promised time onto an order that has already been paid for — the customer
    /// thought of it after the money changed hands.
    /// </summary>
    /// <param name="orderId">The order to annotate.</param>
    /// <param name="phone">Whatever was typed, or <c>null</c> to leave the number alone.</param>
    /// <param name="requestedAt">The promised time, or <c>null</c> to leave it alone.</param>
    /// <param name="promoteToTakeaway">
    /// The operator's explicit agreement to change the order from counter service to takeaway because a
    /// number was named. A phone on a counter-service order is REFUSED without this — see the remarks.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <remarks>
    /// <para>
    /// <b>THE PHONE CONTOUR IS ENFORCED HERE, NOT IN THE SHEET.</b> 152-ФЗ ст. 6(1)(5) permits a phone
    /// only where it is needed for the contract, and counter service needs none: the customer is on the
    /// premises. <c>CheckoutService</c> drops it at the service boundary for that reason, and this
    /// method does not get to be the softer door. A number therefore only lands when the order is
    /// Takeaway, or when the operator has passed <paramref name="promoteToTakeaway"/> to make it so
    /// deliberately. The alternative — letting the sheet write whatever it likes — would put the one
    /// legally meaningful rule in a file that no test covers and that the next screen will copy.
    /// </para>
    /// <para>
    /// <b>NO STATUS CHANGE AND NO HISTORY ROW.</b> The order does not move and no
    /// <c>OrderStatusHistory</c> row is written: the status genuinely did not change, and a history that
    /// claims otherwise is a worse record than no history. What DID change is personal data on a closed
    /// sale, which is why <paramref name="promoteToTakeaway"/> has to be explicit.
    /// </para>
    /// </remarks>
    Task<Order> AddContactDetailsAsync(
        Guid orderId,
        string? phone,
        DateTimeOffset? requestedAt,
        bool promoteToTakeaway = false,
        CancellationToken cancellationToken = default);
}
