using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.ViewModels;

/// <summary>
/// One line of the shift's «Скидки» section: an order line whose charged price differed from the
/// price it was allowed to be sold at.
/// </summary>
/// <remarks>
/// THE WHOLE OF THE PRICE CONTROL, AND IT IS DETECTION, NOT PREVENTION. Nothing here refuses
/// anything at the till and nothing here can be told apart from a legitimately re-priced dish: the
/// domain hands over the lines where <c>PriceKopecks != ListPriceKopecks</c> and this row reports
/// them. There is no reason code and no manager PIN, deliberately — a justification collected at the
/// till becomes the first option clicked and controls nothing, and there is no operator entity in
/// this project to hold an approval.
/// <para>
/// VOIDED ROWS ARE HERE ON PURPOSE, and they are marked. A cancelled sale's money went back, which
/// makes it absent from the revenue — and that is exactly why it is worth looking at: an overridden
/// price on a sale that was then voided is the shape of a mistake twice over. Each row therefore
/// carries its order's status, and nothing in this section may be summed: a voided row's discount is
/// money that was never kept, and adding it to a real sale's would report a figure nobody received.
/// </para>
/// <para>
/// The bundle column is a SECOND, INDEPENDENT signal and is labelled as such. It is the sum of the
/// dishes' à la carte prices — what this composition would have cost on its own — and it is a
/// different figure from the discount: a bundle priced at exactly the sum of its slots has nothing to
/// report there and everything to report in the discount column if its price was touched afterwards.
/// A bundle that is correctly priced but deliberately cheaper than its parts appears in NEITHER, which
/// is what the domain's filter asks for and is documented rather than an omission.
/// </para>
/// </remarks>
public sealed class DiscountedLineRow
{
    public DiscountedLineRow(DiscountedLine model, string orderPrefix)
    {
        Model = model;
        OrderTitle = $"{orderPrefix} #{model.OrderNumber}";
        LineTitle = model.ProductName;
        QuantityText = $"× {model.Quantity}";

        AllowedPriceText = TextFormat.Money(Money.FromKopecks(model.ListPriceKopecks));
        ChargedPriceText = TextFormat.Money(Money.FromKopecks(model.PriceKopecks));

        // Signed and named, because a price can be RAISED as well as lowered and a column labelled
        // «Скидка» showing a positive number is a lie of omission. Negative means the line was
        // charged MORE than it was allowed to be — which is worth seeing too, and rarer.
        var difference = model.DiscountKopecks;
        DifferenceLabel = difference > 0 ? "Скидка" : difference < 0 ? "Наценка" : "Разница";
        DifferenceText = TextFormat.Money(Money.FromKopecks(Math.Abs(difference)));

        ModifierText = DescribeModifier(model.ModifierName, model.VariantName);
    }

    /// <summary>The row as the domain reported it. Never edited.</summary>
    public DiscountedLine Model { get; }

    /// <summary>Which order, in the form the operator reads on a receipt.</summary>
    public string OrderTitle { get; }

    /// <summary>The dish the line was sold on.</summary>
    public string LineTitle { get; }

    /// <summary>
    /// Units on the line. Shown even at 1, and labelled like every other figure on the row rather
    /// than folded into the title: this is an audit list, and a field that only appears when its value
    /// is "interesting" is a field the reader has to wonder about when it is absent.
    /// </summary>
    public string QuantityText { get; }

    /// <summary>Variant and modifier, or empty when the line had neither.</summary>
    public string ModifierText { get; }

    public bool HasModifierText => !string.IsNullOrWhiteSpace(ModifierText);

    /// <summary>The unit price the line was allowed to be sold at.</summary>
    public string AllowedPriceText { get; }

    /// <summary>The unit price actually charged.</summary>
    public string ChargedPriceText { get; }

    /// <summary>«Скидка», «Наценка» or «Разница» — whichever the direction of the change calls for.</summary>
    public string DifferenceLabel { get; }

    /// <summary>The size of the change, as an absolute amount. The direction is in the label.</summary>
    public string DifferenceText { get; }

    /// <summary>
    /// True for a voided order. Shown as a pill rather than folded into the row's styling, so the
    /// state is carried by a word and never by a colour.
    /// </summary>
    public bool IsVoided => Model.Status == OrderStatus.Cancelled;

    public string VoidedText => "отменён";

    /// <summary>The screen-reader wording, which says more than the pill can.</summary>
    public string VoidedHint => "Заказ отменён. Эта строка не входит в выручку и не суммируется.";

    /// <summary>True for a bundle line, i.e. one that has a composition to compare against.</summary>
    public bool HasBundleText => Model.IsBundle;

    /// <summary>
    /// What the same dishes would have cost on their own, and what that makes the bundle worth. The
    /// second signal, and deliberately not the same figure as the discount — see the remarks on the
    /// class.
    /// </summary>
    public string BundleText
    {
        get
        {
            if (Model.ReferenceTotalKopecks is not { } reference) return string.Empty;

            var apart = TextFormat.Money(Money.FromKopecks(reference));
            var saving = Model.BundleSavingKopecks ?? 0;

            if (saving > 0)
                return $"По частям {apart} — на {TextFormat.Money(Money.FromKopecks(saving))} дешевле";
            if (saving < 0)
                return $"По частям {apart} — на {TextFormat.Money(Money.FromKopecks(-saving))} дороже";
            return $"По частям столько же: {apart}";
        }
    }

    /// <summary>
    /// The variant and modifier the line was sold with, in the same order the domain composes them.
    /// Empty rather than "Без модификатора": the label is hidden entirely in that case, and a row
    /// claiming a modifier it does not have is worse than a row that says nothing about one.
    /// </summary>
    private static string DescribeModifier(string? modifierName, string? variantName)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(variantName)) parts.Add(variantName!);
        if (!string.IsNullOrWhiteSpace(modifierName)) parts.Add(modifierName!);
        return parts.Count == 0 ? string.Empty : string.Join(" / ", parts);
    }
}
