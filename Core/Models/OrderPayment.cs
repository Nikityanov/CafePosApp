using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>One payment received against an order — the ledger behind `Order.PaidKopecks`.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed class OrderPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Explicit CLR property rather than a shadow foreign key.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public Guid OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>Amount actually applied, in kopecks, never above the balance of the order at the moment of the payment.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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

    /// <summary>This row pays money OUT of the till rather than into it — money that came back to the customer.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public bool IsRefund { get; set; }

    /// <summary>Free-text reason typed by the operator: why the money went back, or — on an automatic refund written by a cancellation — what caused the cancellation. Nullable because a payment has no reason to give.</summary>

    public string? Note { get; set; }
}
