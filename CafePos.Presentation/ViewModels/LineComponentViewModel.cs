using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePos.Presentation.ViewModels;
/// <summary>
/// One component of a bundle line, printed indented under the line it belongs to.
/// </summary>
/// <remarks>
/// The cashier has to see what they are selling: a bundle that prints as one line is a bundle the
/// kitchen cannot make. One type for the cart and for the open-order editor, because the row is the
/// same two facts — a dish and how many of it go into one set — and a second type would be a second
/// thing to keep in step the first time the wording changes.
/// </remarks>
public sealed class LineComponentViewModel
{
    public LineComponentViewModel(
        Guid productId,
        string name,
        int quantityPerUnit,
        long unitKopecks,
        long referenceKopecks)
    {
        ProductId = productId;
        Name = name;
        QuantityPerUnit = quantityPerUnit;
        UnitKopecks = unitKopecks;
        ReferenceKopecks = referenceKopecks;
    }

    public Guid ProductId { get; }

    public string Name { get; }

    /// <summary>
    /// How many of this dish one unit of the bundle takes. Always at least 1, and always printed —
    /// the multiplicity lives in the slot and is the reason a combo can hold two croissants, so
    /// showing "Круассан" without "2 ×" would throw away the only fact the row has.
    /// </summary>
    public int QuantityPerUnit { get; }

    /// <summary>
    /// What this slot is charged inside the bundle, in kopecks. Carried so the cart hands
    /// <see cref="CheckoutLine.Components"/> back complete; the sale resolves its own figures from the
    /// catalogue and discards these, which is the point of
    /// <see cref="IComboService.ResolveSaleComponentsAsync"/>.
    /// </summary>
    public long UnitKopecks { get; }

    /// <summary>What this dish costs on its own — the figure a bundle's saving is measured against.</summary>
    public long ReferenceKopecks { get; }

    public string Text => $"{QuantityPerUnit} × {Name}";

    public static List<LineComponentViewModel> FromLine(IReadOnlyList<CheckoutComponent>? components) =>
        components is null
            ? []
            : [.. components.Select(component => new LineComponentViewModel(
                component.ProductId,
                component.ProductName,
                component.QuantityPerUnit,
                component.UnitPriceKopecks,
                component.ReferencePriceKopecks))];

    public static List<LineComponentViewModel> FromItem(IReadOnlyList<OrderItemComponent>? components) =>
        components is null
            ? []
            : [.. components.OrderBy(component => component.SortOrder)
                .Select(component => new LineComponentViewModel(
                    component.ProductId,
                    component.ProductName,
                    component.QuantityPerUnit,
                    component.UnitPriceKopecks,
                    component.ReferencePriceKopecks))];
}
