namespace CafePos.Core.Models;

/// <summary>One slot of a : a real product, how many of it per unit of the bundle, and optionally what that slot is charged instead of the dish's own price.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public class ComboComponent
{
    public Guid Id { get; set; }
    public Guid ComboId { get; set; }
    public Combo Combo { get; set; } = null!;

    /// <summary>The dish in this slot.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public Guid ProductId { get; set; }

    /// <summary>Loaded when the catalogue screen reads the bundle. The product identity that survives into the order is a snapshot of name and price, not this reference.</summary>

    public Product? Product { get; set; }

    /// <summary>How many of this dish one unit of the bundle takes.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public int QuantityPerUnit { get; set; }

    /// <summary>What this slot is charged inside the bundle, in kopecks, or `null` to charge the dish's own price.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public long? ComponentPriceKopecks { get; set; }

    /// <summary>What to sell instead when this dish is sold out, or `null` when there is nothing to fall back to.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public Guid? SubstituteProductId { get; set; }

    /// <summary>Loaded with <see cref="Product"/>. Nullable on purpose: most slots have none.</summary>
    public Product? SubstituteProduct { get; set; }
}
