using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>What a bundle prints about what is INSIDE it: the slots, in the one order a bundle reads in.</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public static class ComboComposition
{
    /// <summary>How many characters the composition line may use.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    [Obsolete("The combo tile is full width and wraps; there is no character budget. Kept only to name what was removed and why.", false)]
    public const int TileCharBudget = 24;

    /// <summary>The slots in the order a bundle reads in.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static IOrderedEnumerable<ComboComponent> CatalogueOrder(IEnumerable<ComboComponent> components) =>
        components
            .OrderBy(component => component.Product?.Name, StringComparer.CurrentCulture)
            .ThenBy(component => component.ProductId);

    /// <remarks>`docs/decisions/combos.md`</remarks>

    public static string Summarise(IEnumerable<ComboComponent> components)
    {
        var names = CatalogueOrder(components)
            .Select(slot => SlotName(slot.Product?.Name ?? slot.ProductId.ToString(), slot.QuantityPerUnit))
            .ToList();

        return string.Join(", ", names);
    }

    /// <summary>«Круассан», or «2 × Круассан» when the slot takes more than one.</summary>
    private static string SlotName(string name, int quantityPerUnit) =>
        quantityPerUnit > 1 ? $"{quantityPerUnit} × {name}" : name;
}
