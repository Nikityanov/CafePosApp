namespace CafePos.Core.Models;

/// <summary>Status transition log. Orders now carry a full audit trail (who changed what and when) instead of only the current status.</summary>

public class OrderStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Order? Order { get; set; }
    public OrderStatus Status { get; set; }
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Optional comment (for example the cancellation reason).</summary>
    public string? Comment { get; set; }
}
