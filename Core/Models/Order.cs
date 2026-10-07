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

    /// <summary>When somebody last opened this order AFTER it had become ready, or null if nobody has.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public DateTimeOffset? SeenAt { get; set; }

    /// <summary>Whether this order became ready and has not been opened since — the unread dot on the board.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public bool IsUnseenReady(DateTimeOffset now) =>
        Status == OrderStatus.Ready && ReadyAt is { } ready && (SeenAt is not { } seen || seen < ready)
        && now >= ready;

    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public OrderStatus Status { get; set; }

    /// <summary>Whether the customer eats here or takes it away. Decides whether a phone is kept.</summary>
    public OrderType OrderType { get; set; }

    /// <summary>Contact for an `OrderType.Takeaway` order, in E.164 (see `PhoneNumber.Normalize`) — or `null`, which is a normal state and not a missing value.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public string? CustomerPhone { get; set; }

    /// <summary>The time the customer asked for, or `null` for "as soon as possible".</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>How many minutes an order without a requested time is promised to take.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public static int LeadTimeMinutes { get; set; } = DefaultLeadTimeMinutes;

    /// <summary>The value <see cref="LeadTimeMinutes"/> starts from, and the figure shown to the customer.</summary>
    public const int DefaultLeadTimeMinutes = 10;

    // When this order is meant to reach the customer: what was asked for, or otherwise the standard promise counted from when it was created.
    // Почему так — `docs/decisions/orders.md`

    [NotMapped]
    public DateTimeOffset PromisedAt => RequestedAt ?? CreatedAt.AddMinutes(LeadTimeMinutes);

    /// <summary>Whether the customer asked for a time in the future, which is what puts the order in the "by the time" section of the queue rather than in the working…</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public bool IsScheduledAt(DateTimeOffset now) => RequestedAt.HasValue && RequestedAt > now;

    /// <summary>Whether the promise has already passed while the order is still being made.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public bool IsOverdueAt(DateTimeOffset now) => PromisedAt < now;

    /// <summary>Order total in kopecks — makes SUM/ORDER BY work in SQLite.</summary>
    public long TotalKopecks { get; set; }

    [NotMapped]
    public decimal TotalPrice
    {
        get => Money.FromKopecks(TotalKopecks);
        set => TotalKopecks = Money.ToKopecks(value);
    }

    /// <summary>NET amount still held for this order, in kopecks: what was collected MINUS what was refunded.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public long PaidKopecks { get; set; }

    // What is still owed. Scalar only — never SUM(Payments), see <see cref="PaidKopecks"/>.
    [NotMapped]
    public long BalanceKopecks => Math.Max(0, TotalKopecks - PaidKopecks);

    // A zero-total order is trivially paid: there is nothing to collect, and blocking its status advance would strand an order nobody can ever pay for.

    [NotMapped]
    public bool IsFullyPaid => PaidKopecks >= TotalKopecks;

    [NotMapped]
    public PaymentState PaymentState => PaidKopecks <= 0
        ? PaymentState.Unpaid
        : IsFullyPaid ? PaymentState.Paid : PaymentState.PartiallyPaid;

    public List<OrderItem> Items { get; set; } = new();
    public List<OrderStatusHistory> StatusHistory { get; set; } = new();

    /// <summary>Ledger of the order.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public List<OrderPayment> Payments { get; set; } = new();

    public Guid? ShiftId { get; set; }
    public Shift? Shift { get; set; }

    /// <summary>Recalculates `TotalKopecks` from the current items (integer arithmetic).</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public void RecalculateTotal(IEnumerable<OrderItem>? lines = null) =>
        TotalKopecks = (lines ?? Items).Sum(item => item.LineTotalKopecks);
}
