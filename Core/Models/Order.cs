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

    public List<OrderItem> Items { get; set; } = new();
    public List<OrderStatusHistory> StatusHistory { get; set; } = new();
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
