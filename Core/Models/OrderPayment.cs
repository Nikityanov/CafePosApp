using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// One payment received against an order — the ledger behind <see cref="Order.PaidKopecks"/>.
/// The two must always agree, so every write goes through the single mutation site
/// (<c>Services.PaymentRecorder</c>), which adds the row and moves the scalar together.
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
}
