using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// Records a price change for a product.
/// </summary>
public class PriceHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public long OldPriceKopecks { get; set; }
    public long NewPriceKopecks { get; set; }

    [NotMapped]
    public decimal OldPrice
    {
        get => Money.FromKopecks(OldPriceKopecks);
        set => OldPriceKopecks = Money.ToKopecks(value);
    }

    [NotMapped]
    public decimal NewPrice
    {
        get => Money.FromKopecks(NewPriceKopecks);
        set => NewPriceKopecks = Money.ToKopecks(value);
    }

    /// <summary>UTC timestamp of the change.</summary>
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// One-line summary of the change, e.g. "12.05.2026: 200 → 250 ₽" or "… → 250 ₿". Bound
    /// directly so the history list does not need a MultiBinding over three paths.
    /// </summary>
    /// <remarks>
    /// Whole units on both sides, deliberately: this is a price-change audit line, and "200 → 250"
    /// is what a manager reads. The symbol comes from <see cref="Currencies.Default"/> via
    /// <see cref="TextFormat"/>, so it follows the operator's setting.
    /// </remarks>
    [NotMapped]
    public string ChangeText
    {
        get
        {
            var symbol = Currencies.Default.Symbol;
            return $"{ChangedAt.ToLocalTime():dd.MM.yyyy}: {OldPrice:0} → {NewPrice:0} {symbol}";
        }
    }
}
