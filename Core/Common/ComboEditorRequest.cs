using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>A dish a bundle slot may be filled with, as the composition sheet offers it.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed record ComboSlotOption(Guid ProductId, string Label, long UnitKopecks, long ReferenceKopecks);

/// <summary>One slot of a bundle as the cashier left it: which dish, and how many of it per unit.</summary>
public sealed record ComboSlotChoice(Guid ProductId, int QuantityPerUnit);

/// <summary>What the composition sheet opens on: the dishes it may offer, what is in the sheet already, and the bundle's own price.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed record ComboEditorRequest(
    string Title,
    IReadOnlyList<ComboSlotOption> Options,
    IReadOnlyList<ComboSlotChoice> Selection,
    long PriceKopecks = 0)
{
    /// <summary>An empty build: the sheet with nothing chosen yet.</summary>
    public static ComboEditorRequest Empty { get; } = new("Состав комбо", [], []);
}
