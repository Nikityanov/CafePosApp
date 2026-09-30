using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// Time-based pricing rule for a product.
/// Applied when the rule's time window is active.
/// </summary>
public class PriceRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Optional: specific day of week. Null = every day.
    /// </summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// Start hour (0-23). Inclusive.
    /// </summary>
    public int StartHour { get; set; }

    /// <summary>
    /// End hour (0-23). Exclusive. Supports overnight ranges (e.g., 22→6).
    /// </summary>
    public int EndHour { get; set; }

    /// <summary>Absolute price override in kopecks. If set, takes precedence over adjustments.</summary>
    public long? PriceOverrideKopecks { get; set; }

    [NotMapped]
    public decimal? PriceOverride
    {
        get => PriceOverrideKopecks is null ? null : Money.FromKopecks(PriceOverrideKopecks.Value);
        set => PriceOverrideKopecks = value is null ? null : Money.ToKopecks(value.Value);
    }

    /// <summary>Price adjustment in kopecks (positive = markup, negative = discount).</summary>
    public long PriceAdjustmentKopecks { get; set; }

    [NotMapped]
    public decimal PriceAdjustment
    {
        get => Money.FromKopecks(PriceAdjustmentKopecks);
        set => PriceAdjustmentKopecks = Money.ToKopecks(value);
    }

    /// <summary>
    /// Percentage markup/discount (e.g., 10 = +10%, -15 = -15%).
    /// Applied if PriceOverride and PriceAdjustment are both 0.
    /// </summary>
    public decimal PercentAdjustment { get; set; }

    public bool IsActive { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    /// <summary>True when the rule's time window covers the given local date/time.</summary>
    public bool IsActiveAt(DateTimeOffset localTime)
    {
        if (DayOfWeek.HasValue && DayOfWeek.Value != localTime.DayOfWeek) return false;
        var hour = localTime.Hour;
        return StartHour <= EndHour
            ? hour >= StartHour && hour < EndHour
            : hour >= StartHour || hour < EndHour;
    }
}
