using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// One bundle as the catalogue list shows it: its name, what it is made of, its own price, and what
/// that price means against the à la carte sum of its parts.
/// </summary>
/// <remarks>
/// A ViewModel rather than the <see cref="Combo"/> entity, because the list's most-wanted figure —
/// the discount — is not on the entity either. The price is <see cref="Combo.PriceKopecks"/>, the
/// number accounting put in the card; the à la carte sum is
/// <see cref="ComboPricing.ReferenceKopecks"/> over the loaded components, and the two together are
/// what <see cref="ComboPricing.DiscountPercent"/> measures.
/// <para>
/// It is recomputed on every catalogue load, which is what makes this row the standing safeguard:
/// <c>CatalogManagementViewModel.LoadAsync</c> re-reads the bundles with their dishes, so changing a
/// dish's price in «Товары» and coming back updates the reference — and the discount — here. If a
/// component's price moves and nobody re-prices the bundle, the operator sees it on this row. There
/// is nothing to press and nothing to invalidate.
/// </para>
/// </remarks>
public sealed class ComboRowViewModel
{
    /// <summary>How many component names are named before the row starts counting instead.</summary>
    private const int MaxNamedSlots = 3;

    public ComboRowViewModel(Combo model)
    {
        Model = model;
        Name = model.Name;
        ReferenceKopecks = ComboPricing.ReferenceKopecks(model.Components.Select(Line));
        PriceText = TextFormat.Money(Money.FromKopecks(model.PriceKopecks));
        DiscountText = DescribeDiscount(model.PriceKopecks, ReferenceKopecks);

        SlotCountText = $"{model.Components.Count} "
                        + TextFormat.Plural(model.Components.Count, "компонент", "компонента", "компонентов");

        // A hand-edited or half-migrated database can leave a slot pointing at a row that is not
        // there. It is named rather than skipped: a bundle whose reference silently ignores a slot
        // reads as a cheaper bundle, which is the one thing on this row nobody may be misled about.
        MissingDishCount = model.Components.Count(slot => slot.Product is null);
        SubstituteCount = model.Components.Count(slot => slot.SubstituteProductId is not null);
        ComponentsText = DescribeComponents(model);
    }

    /// <summary>The row as the catalogue read it. Never edited from here.</summary>
    public Combo Model { get; }

    /// <summary>Stable key for the list's in-place sync.</summary>
    public Guid Id => Model.Id;

    public string Name { get; }

    /// <summary>
    /// The à la carte sum of the slots, in kopecks. A REFERENCE, not a price — it is what the
    /// discount is measured against. Derived on every load — see the remarks on the class.
    /// </summary>
    public long ReferenceKopecks { get; }

    /// <summary>The bundle's own price, in the operator's currency. What the till charges.</summary>
    public string PriceText { get; }

    /// <summary>
    /// What the price works out to against the à la carte sum, in words: «Скидка 19%»,
    /// «Наценка 5%», or empty when there is nothing to compare against. The standing safeguard —
    /// if a component's price moves and nobody re-prices the bundle, this line is where the operator
    /// sees it.
    /// </summary>
    public string DiscountText { get; }

    public string SlotCountText { get; }

    /// <summary>The dishes, with their multiplicities, for as many as fit the row.</summary>
    public string ComponentsText { get; }

    /// <summary>How many slots have no resolvable dish. Non-zero means the row is not trustworthy.</summary>
    public int MissingDishCount { get; }

    public bool HasMissingDish => MissingDishCount > 0;

    public string MissingDishText =>
        $"{MissingDishCount} {TextFormat.Plural(MissingDishCount, "блюдо не найдено", "блюда не найдено", "блюд не найдено")}";

    /// <summary>How many slots name a replacement, i.e. the bundle still sells when that dish runs out.</summary>
    public int SubstituteCount { get; }

    public bool HasSubstitutes => SubstituteCount > 0;

    /// <summary>Marks the bundle as sellable-with-a-fallback rather than blocked when a dish runs out.</summary>
    public string SubstituteText =>
        $"{SubstituteCount} {TextFormat.Plural(SubstituteCount, "замена", "замены", "замен")}";

    /// <summary>One slot as the price is counted: how many, at the DISH's own price.</summary>
    /// <remarks>
    /// The dish's price, not the slot's stored override — for the same reason as in
    /// <c>ComboFormViewModel.UnitKopecks</c>, and the two MUST agree or the list and the form would
    /// quote a different «по отдельноcти» for the same bundle on the same screen. An override the
    /// operator can no longer see or change must not decide the saving in either place.
    /// </remarks>
    private static ComboComponentPrice Line(ComboComponent slot) => new(
        slot.QuantityPerUnit,
        slot.Product?.PriceKopecks ?? 0);

    private static string DescribeComponents(Combo model)
    {
        if (model.Components.Count == 0) return "Состав не задан";

        var named = model.Components
            .Take(MaxNamedSlots)
            .Select(slot => slot.Product is null
                ? "— блюдо не найдено"
                : slot.QuantityPerUnit == 1
                    ? slot.Product.Name
                    : $"{slot.Product.Name} × {slot.QuantityPerUnit}");

        var text = string.Join(" · ", named);
        return model.Components.Count > MaxNamedSlots
            ? $"{text} · и ещё {model.Components.Count - MaxNamedSlots}"
            : text;
    }

    /// <summary>
    /// What the bundle's own price works out to against the à la carte sum, in words. Cheaper is a
    /// discount, dearer is a surcharge (labelled honestly, never as a negative discount), equal says
    /// so plainly, and a zero reference has no percentage at all — «0%» would be a false statement
    /// about a bundle the customer is paying for.
    /// </summary>
    private static string DescribeDiscount(long priceKopecks, long referenceKopecks)
    {
        var percent = ComboPricing.DiscountPercent(referenceKopecks, priceKopecks);
        if (percent is null) return string.Empty;

        var formatted = percent.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        return ComboPricing.Compare(referenceKopecks, priceKopecks) switch
        {
            ComboPriceRelation.Cheaper => $"Скидка {formatted}%",
            ComboPriceRelation.Dearer => $"Наценка {formatted}%",
            _ => "Цена равна сумме компонентов"
        };
    }
}
