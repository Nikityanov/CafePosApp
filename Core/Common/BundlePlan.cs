using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>A catalogue bundle read against what is on the shelf right now: what each slot may hold, what is in it already, what the slots come to à la carte — or…</summary>
/// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

public sealed record BundlePlan(
    IReadOnlyList<ComboSlotOption> Options,
    IReadOnlyList<ComboSlotChoice> Selection,
    long ReferenceKopecks,
    string? BlockedDishName)
{
    /// <summary>True when nothing blocks the sale.</summary>
    public bool CanBeSold => BlockedDishName is null;

    /// <summary>Reads a catalogue bundle against the stock as loaded.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static BundlePlan Describe(Combo template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var options = new List<ComboSlotOption>();
        var selection = new List<ComboSlotChoice>();
        var reference = 0L;

        foreach (var slot in ComboComposition.CatalogueOrder(template.Components))
        {
            var sold = SellableDish(slot);

            // The whole bundle is refused on the first unanswerable slot, not on the last: a sheet
            // offering two of three dishes and then refusing is worse than no sheet.
            if (sold is null)
                return new BundlePlan([], [], 0, SoldOutName(slot));

            // Unwrapped explicitly rather than through `sold.Product`: Nullable<ValueTuple<>> exposes no
            // members, so every read off it has to go through .Value.
            var (product, label) = sold.Value;
            var quantity = Math.Max(1, slot.QuantityPerUnit);

            /// <summary>The dish's price, not the slot's stored override — see ComboFormViewModel.UnitKopecks.</summary>
            /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

            var unit = product.PriceKopecks;

            options.Add(new ComboSlotOption(slot.ProductId, label, unit, product.PriceKopecks));
            selection.Add(new ComboSlotChoice(slot.ProductId, quantity));
            reference += (long)quantity * unit;
        }

        return new BundlePlan(options, selection, reference, null);
    }

    /// <summary>The dish a slot will actually be sold for, or null when neither it nor its substitute is available.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    public static (Product Product, string Label)? SellableDish(ComboComponent slot)
    {
        if (IsSellable(slot.Product)) return (slot.Product!, slot.Product!.Name);

        if (IsSellable(slot.SubstituteProduct))
        {
            var substitute = slot.SubstituteProduct!;
            var replaced = slot.Product?.Name;

            /// <summary>The label says what is being sold AND what it stands in for. A substitution is a decision the operator never made, and a sheet that silently showed another dish's name would sell something the cashier never looked at.</summary>

            return (substitute, replaced is null
                ? substitute.Name
                : $"{substitute.Name} (замена: {replaced})");
        }

        return null;
    }

    private static bool IsSellable(Product? product) => product is { IsAvailable: true, IsDeleted: false };

    /// <summary>The dish that blocks a bundle, named.</summary>
    /// <remarks>Почему так — `docs/decisions/combos.md`</remarks>

    private static string SoldOutName(ComboComponent slot) => slot.Product?.Name ?? slot.ProductId.ToString();
}
