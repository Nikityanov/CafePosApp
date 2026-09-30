using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>How one step of the "add a product to the cart" flow ended.</summary>
public enum AddToCartStepOutcome
{
    /// <summary>The step is satisfied and the flow may move on to the next one.</summary>
    Proceed,

    /// <summary>
    /// The user dismissed the picker. Nothing was chosen, so the whole add is abandoned —
    /// deliberately without a message, because a cancel is not an error.
    /// </summary>
    Dismissed
}

/// <summary>
/// The decisions of the "add a product to the cart" flow: modifier sheet → variant sheet → price.
///
/// The sheets are platform UI, so this type never awaits anything: the caller passes in what each
/// picker returned and gets back the verdict. That split is the point — the three cancel paths are
/// the part of the flow that actually goes wrong, and none of them needs a UI to be reasoned about.
/// </summary>
public sealed class ProductAddFlow
{
    /// <summary>Shown when a product claims to have variants but none of them is purchasable.</summary>
    public const string NoAvailableVariantsMessage = "У блюда нет доступных вариантов.";

    private readonly Product product;

    private ProductAddFlow(Product product, List<ProductVariant> availableVariants)
    {
        this.product = product;
        AvailableVariants = availableVariants;
    }

    public static ProductAddFlow Start(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        // Filtered and ordered once, here, so the sheet and the "is anything purchasable" verdict
        // can never disagree about which variants exist.
        List<ProductVariant> available = [];
        if (product.HasVariants)
        {
            available = [.. product.Variants.Where(variant => variant.IsAvailable).OrderBy(variant => variant.SortOrder)];
        }

        return new ProductAddFlow(product, available);
    }

    /// <summary>
    /// The group the modifier sheet must offer, or <c>null</c> when the product has none and the
    /// whole step is skipped.
    /// </summary>
    public ModifierGroup? ModifierGroup => product.ModifierGroup;

    /// <summary>
    /// True when the variant step runs at all. This is the product's own
    /// <see cref="Product.HasVariants"/> flag, not "are there purchasable variants" — the two can
    /// disagree, which is exactly what <see cref="CanOfferVariantChoice"/> has to catch.
    /// </summary>
    public bool RequiresVariantChoice => product.HasVariants;

    /// <summary>
    /// The purchasable variants, in the order the sheet lists them.
    /// </summary>
    public List<ProductVariant> AvailableVariants { get; }

    /// <summary>
    /// Whether the variant step can be completed at all. A product flagged as having variants whose
    /// variants are all sold out has nothing to show and no price to charge, so the add fails
    /// outright. That is a different outcome from a dismiss and the caller has to say why.
    /// </summary>
    public bool CanOfferVariantChoice => !RequiresVariantChoice || AvailableVariants.Count > 0;

    /// <summary>
    /// Feeds the modifier picker's result back in. A null result only counts as a dismiss when
    /// there was a sheet to dismiss: without a group the step never ran, and "nothing chosen" is
    /// the normal answer.
    /// </summary>
    public AddToCartStepOutcome SubmitModifier(string? picked) =>
        ModifierGroup is not null && picked is null ? AddToCartStepOutcome.Dismissed : AddToCartStepOutcome.Proceed;

    /// <summary>
    /// Feeds the variant picker's result back in. Unlike the modifier step there is no "leave it
    /// empty" option — a variant is required — so a null result can only mean the sheet went away.
    /// </summary>
    public AddToCartStepOutcome SubmitVariant(string? picked) =>
        picked is null ? AddToCartStepOutcome.Dismissed : AddToCartStepOutcome.Proceed;

    /// <summary>
    /// The price the cart line is charged. A chosen variant replaces the product price outright
    /// rather than adding to it; an unrecognised name (a picker that returned something the catalog
    /// does not have) falls back to the product price instead of charging zero.
    /// </summary>
    public decimal ResolvePrice(string? variant) => variant is null
        ? product.Price
        : product.Variants.FirstOrDefault(item => item.Name == variant)?.Price ?? product.Price;
}
