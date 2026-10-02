using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// One payment received against an order — the ledger behind <see cref="Order.PaidKopecks"/>.
/// The two must always agree, so every write goes through the single mutation site
/// (<c>Services.PaymentRecorder</c>), which adds the row and moves the scalar together.
/// <para>
/// A refund is the same row shape with <see cref="IsRefund"/> set, not a separate table: the
/// reconciliation invariant is a signed sum over one ledger, and a second table would make every
/// report that forgets to join it over-report the money that came in.
/// </para>
/// </summary>
public sealed class OrderPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Explicit CLR property rather than a shadow foreign key. A shadow property still produces a
    /// column, but it is invisible in the entity, in code navigation and in the model/migration
    /// parity check that reads the EF model — which is exactly why the parity test counts shadow
    /// properties as columns. Declaring it keeps the FK visible where the code reads it.
    /// </summary>
    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>
    /// Amount actually applied, in kopecks, never above the balance of the order at the moment of
    /// the payment. A POS is regularly handed a 1000 ₽ note for a 660 ₽ order; the surplus is
    /// change the UI shows and does not pass in, and the clamp here is the defensive second line.
    /// </summary>
    /// <para>
    /// Positive on every row, refund rows included. A refund is a row that pays money OUT, never a
    /// negative receipt: <see cref="IsRefund"/> carries the direction. Keeping the amount positive
    /// means the invariant stays a plain signed sum, and no report that forgets to filter can
    /// accidentally double-count by treating a refund as money coming in.
    /// </para>
    public long AmountKopecks { get; set; }

    [NotMapped]
    public decimal Amount
    {
        get => Money.FromKopecks(AmountKopecks);
        set => AmountKopecks = Money.ToKopecks(value);
    }

    public PaymentMethod Method { get; set; }

    /// <summary>UTC. Convert to local time in the presentation layer.</summary>
    public DateTimeOffset PaidAt { get; set; }

    /// <summary>
    /// This row pays money OUT of the till rather than into it — money that came back to the
    /// customer. Stored as a plain SQLite INTEGER with no value converter, following
    /// <c>DraftOrder.IsActiveCart</c>: the whole point is that "IsRefund = 0" is the correct value
    /// for every row that already exists, so no backfill and no enum-name strings are involved.
    /// A refund mirrors the methods of the payments it reverses (see
    /// <c>OrderService.RefundAsync</c>), so it is never an operator-chosen method.
    /// </summary>
    public bool IsRefund { get; set; }

    /// <summary>
    /// Free-text reason typed by the operator: why the money went back, or — on an automatic
    /// refund written by a cancellation — what caused the cancellation. Nullable because a payment
    /// has no reason to give.
    /// </summary>
    public string? Note { get; set; }
}
