namespace CafePos.Core.Models;

public class Shift
{
    public Guid Id { get; set; }

    /// <summary>Shift start (UTC).</summary>
    public DateTimeOffset StartTime { get; set; }

    public DateTimeOffset? EndTime { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// Next order number for this shift. Incremented atomically in SQL
    /// (UPDATE ... RETURNING) so parallel checkouts can never take the same number.
    /// </summary>
    public int NextOrderNumber { get; set; } = 1;

    public List<Order> Orders { get; set; } = new();
}
