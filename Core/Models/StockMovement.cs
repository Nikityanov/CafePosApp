namespace CafePos.Core.Models;

/// <summary>Audit trail of every stock change (order write-off, delivery, manual correction). Without it a mismatch between the recipe and the shelf cannot be investigated.</summary>

public class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IngredientId { get; set; }
    public Ingredient? Ingredient { get; set; }

    /// <summary>Signed amount in ingredient units: negative = write-off, positive = delivery.</summary>
    public decimal QuantityDelta { get; set; }

    /// <summary>Stock level after the change, for a readable journal.</summary>
    public decimal StockAfter { get; set; }

    /// <summary>Human readable reason, e.g. "Заказ #12" or "Поставка".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>What this row is, so nothing has to decide it by reading <see cref="Reason"/>.</summary>

    public StockMovementKind Kind { get; set; } = StockMovementKind.Unknown;

    public Guid? OrderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
