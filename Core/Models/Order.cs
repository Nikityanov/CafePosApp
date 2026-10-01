using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

public class Order
{
    public Guid Id { get; set; }
    public int OrderNumber { get; set; }

    /// <summary>Creation timestamp (UTC). Convert to local time in the presentation layer.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public OrderStatus Status { get; set; }

    /// <summary>Order total in kopecks — makes SUM/ORDER BY work in SQLite.</summary>
    public long TotalKopecks { get; set; }

    [NotMapped]
    public decimal TotalPrice
    {
        get => Money.FromKopecks(TotalKopecks);
        set => TotalKopecks = Money.ToKopecks(value);
    }

    /// <summary>
    /// NET amount still held for this order, in kopecks: what was collected MINUS what was refunded.
    /// Net is the figure every screen reads, because it is what the till still owes the customer —
    /// after a partial refund the order is still paid for, just for less. The ledger reconciles
    /// against it as <c>SUM(Where(!IsRefund)) − SUM(Where(IsRefund))</c>; the direction lives in
    /// <see cref="OrderPayment.IsRefund"/>, never in a negative amount.
    /// <para>
    /// Persisted on purpose: every list that shows a payment state (GetActiveOrdersAsync, the orders
    /// screen) loads orders WITHOUT their payments, and a value derived from the
    /// <see cref="Payments"/> collection would read zero there — silently, with no error anywhere,
    /// so every card would claim "unpaid" while the money is on the table. EF only links tracked
    /// entities into a loaded collection during DetectChanges, so an un-Included collection is not
    /// merely stale, it is empty.
    /// </para>
    /// </summary>
    public long PaidKopecks { get; set; }

    /// <summary>What is still owed. Scalar only — never SUM(Payments), see <see cref="PaidKopecks"/>.</summary>
    [NotMapped]
    public long BalanceKopecks => Math.Max(0, TotalKopecks - PaidKopecks);

    /// <summary>
    /// A zero-total order is trivially paid: there is nothing to collect, and blocking its status
    /// advance would strand an order nobody can ever pay for.
    /// </summary>
    [NotMapped]
    public bool IsFullyPaid => PaidKopecks >= TotalKopecks;

    [NotMapped]
    public PaymentState PaymentState => PaidKopecks <= 0
        ? PaymentState.Unpaid
        : IsFullyPaid ? PaymentState.Paid : PaymentState.PartiallyPaid;

    public List<OrderItem> Items { get; set; } = new();
    public List<OrderStatusHistory> StatusHistory { get; set; } = new();

    /// <summary>
    /// Ledger of the order. DISPLAY ONLY: populate it with an explicit <c>Include</c> (or on a
    /// freshly added parent). It is intentionally absent from the queries behind the order lists —
    /// nothing in the domain reads it, and <see cref="PaidKopecks"/> is the value every screen
    /// must trust. See the remarks on <see cref="PaidKopecks"/> for what an un-Included collection
    /// silently returns.
    /// </summary>
    public List<OrderPayment> Payments { get; set; } = new();

    public Guid? ShiftId { get; set; }
    public Shift? Shift { get; set; }

    /// <summary>
    /// Recalculates <see cref="TotalKopecks"/> from the current items (integer arithmetic).
    /// Pass an explicit line set when the new lines are not in <see cref="Items"/> yet — EF only
    /// links a tracked entity into the loaded collection when it runs DetectChanges, and pushing
    /// the same entity into the collection by hand would count it twice.
    /// </summary>
    public void RecalculateTotal(IEnumerable<OrderItem>? lines = null) =>
        TotalKopecks = (lines ?? Items).Sum(item => item.LineTotalKopecks);
}
