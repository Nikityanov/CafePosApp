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

    /// <summary>
    /// When somebody last opened this order AFTER it had become ready, or <c>null</c> if nobody has.
    /// </summary>
    /// <remarks>
    /// <b>THIS EXISTS ONLY TO ANSWER ONE QUESTION: "HAS ANYONE SEEN THIS SINCE IT GOT READY?"</b>
    /// <see cref="ReadyAt"/> alone cannot answer that — it says when the order changed, not whether
    /// anybody noticed. The board draws an unread dot while
    /// <c>Status == Ready &amp;&amp; (SeenAt is null || SeenAt &lt; ReadyAt)</c>, which is the Toast
    /// pattern: a marker that waits to be acknowledged rather than one that fades.
    /// <para>
    /// Stored, not held in memory. The board rebuilds its rows on an auto-refresh tick, so an in-memory
    /// flag would be lost every few seconds and on every restart — and a dot that forgets itself trains
    /// the operator to ignore it, which is worse than having none.
    /// </para>
    /// <para>
    /// Comparing the two instants rather than keeping a boolean is what lets the dot come BACK: an order
    /// that goes back to preparing and then becomes ready again has a new <see cref="ReadyAt"/>, so
    /// <see cref="SeenAt"/> now compares as older and the dot returns. A bool would have to be reset by
    /// the status change itself, and every future path that moved an order to ready would have to
    /// remember to.
    /// </para>
    /// </remarks>
    public DateTimeOffset? SeenAt { get; set; }

    /// <summary>
    /// Whether this order became ready and has not been opened since — the unread dot on the board.
    /// </summary>
    /// <remarks>
    /// A pure function of the two stored moments and the current status, so it cannot drift from them
    /// the way a maintained flag can. Takes <paramref name="now"/> for the same reason
    /// <see cref="IsScheduledAt"/> does: an entity has no clock, and a test must be able to place
    /// itself on either side of the boundary.
    /// </remarks>
    public bool IsUnseenReady(DateTimeOffset now) =>
        Status == OrderStatus.Ready && ReadyAt is { } ready && (SeenAt is not { } seen || seen < ready)
        && now >= ready;

    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public OrderStatus Status { get; set; }

    /// <summary>Whether the customer eats here or takes it away. Decides whether a phone is kept.</summary>
    public OrderType OrderType { get; set; }

    /// <summary>
    /// Contact for an <see cref="OrderType.Takeaway"/> order, in E.164 (see
/// <see cref="PhoneNumber.Normalize"/>) — or <c>null</c>, which is a normal state and not
    /// a missing value.
    /// <para>
    /// <b>WRITTEN ONLY FOR TAKEAWAY, AND THAT IS A LEGAL REQUIREMENT, NOT A TIDINESS RULE.</b>
    /// 152-ФЗ ст. 6(1)(5) permits a phone to be processed only where it is needed for the performance
    /// of the contract, and ст. 5(7) requires erasing it once that purpose is met. Counter service
    /// does not need a number for anything — it is on the premises — so
    /// <c>CheckoutService</c> drops it at the service boundary instead of writing a row it would
    /// then have to erase. CoAP 13.11 ч.12 prices unlawful disclosure at 3–5 million ₽ for
    /// 1 000–10 000 subjects and counts ROWS, so the counter is how many rows exist, not whether the
    /// column does.
    /// </para>
    /// </summary>
    public string? CustomerPhone { get; set; }

    /// <summary>
    /// The time the customer asked for, or <c>null</c> for "as soon as possible".
    /// <para>
    /// <c>null</c> is not an absent value that needs filling in: Toast models the same thing as a
    /// lead time rather than as a flag, where <c>prep time = quote time + lead time</c>, and our
    /// equivalent is <c>RequestedAt == null</c> with the promise counted as
    /// <c>CreatedAt + <see cref="LeadTimeMinutes"/></c>. An "ASAP" flag would store the same absence
    /// twice and the two could disagree.
    /// </para>
    /// <para>
    /// It is deliberately not the same field as the promise. Oracle Communications OSM keeps
    /// <c>RequestedDate</c> and <c>Promise Date</c> apart for the same reason: when they differ,
    /// one of them is a mistake, and a single column cannot say which.
    /// </para>
    /// </summary>
    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>
    /// How many minutes an order without a requested time is promised to take. A setting and not a
    /// column, with 10 as the figure an ordinary drink is quoted at.
    /// <para>
    /// Ambient and mutable for the reason <see cref="Currencies.Default"/> is: an entity has no
    /// constructor to receive it through, and <see cref="PromisedAt"/> is a derived property read
    /// without a way to pass anything in. It is assigned once at startup from
    /// <c>DatabaseOptions.LeadTimeMinutes</c> and never from the database, so no order row can change
    /// it after the fact and a promise computed today cannot disagree with the figure quoted when the
    /// order was taken.
    /// </para>
    /// </summary>
    public static int LeadTimeMinutes { get; set; } = DefaultLeadTimeMinutes;

    /// <summary>The value <see cref="LeadTimeMinutes"/> starts from, and the figure shown to the customer.</summary>
    public const int DefaultLeadTimeMinutes = 10;

    /// <summary>
    /// When this order is meant to reach the customer: what was asked for, or otherwise the standard
    /// promise counted from when it was created. Not stored, because it is arithmetic on
    /// <see cref="RequestedAt"/>, <see cref="CreatedAt"/> and <see cref="LeadTimeMinutes"/> — a
    /// stored copy would be a fourth place for the queue order and the report to disagree with.
    /// </summary>
    [NotMapped]
    public DateTimeOffset PromisedAt => RequestedAt ?? CreatedAt.AddMinutes(LeadTimeMinutes);

    /// <summary>
    /// Whether the customer asked for a time in the future, which is what puts the order in the
    /// "by the time" section of the queue rather than in the working one.
    /// <para>
    /// Takes <paramref name="now"/> as an argument because an entity has no clock of its own: the
    /// app's single clock is the injected <see cref="TimeProvider"/>, and passing the moment in keeps
    /// this a pure function that a test can place on either side of the boundary.
    /// </para>
    /// </summary>
    public bool IsScheduledAt(DateTimeOffset now) => RequestedAt.HasValue && RequestedAt > now;

    /// <summary>
    /// Whether the promise has already passed while the order is still being made.
    /// <para>
    /// A display state and NOT a validation failure: no vendor rejects an order whose promised time
    /// has elapsed in the queue — the kitchen is behind, which is a fact about the kitchen. It is
    /// also a different measurement from the order's age in the queue; the two timers are kept apart
    /// because a colour on the kitchen screen measures how long the item has been cooking, not how
    /// late the promise is.
    /// </para>
    /// </summary>
    public bool IsOverdueAt(DateTimeOffset now) => PromisedAt < now;

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
