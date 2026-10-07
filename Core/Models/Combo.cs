namespace CafePos.Core.Models;

/// <summary>A bundle in the catalogue: a template with a price of its own, , and a set of it is sold as.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public class Combo
{
    public Guid Id { get; set; }

    /// <summary>Catalogued name, matching the bound of <see cref="Product.Name"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the till charges for one unit of this bundle, in kopecks.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public long PriceKopecks { get; set; }

    /// <summary>Soft delete: sold combos stay on historical receipts.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Order in the catalogue list. Ties are broken by name, so gaps are harmless.</summary>
    public int SortOrder { get; set; }

    /// <summary>The slots this bundle is made of.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public List<ComboComponent> Components { get; set; } = new();
}
