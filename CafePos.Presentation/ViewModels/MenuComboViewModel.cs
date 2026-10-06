using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// One bundle on the menu board: its name, what is in it, its own price, and whether it can be sold
/// at all right now.
/// </summary>
/// <remarks>
/// The price is the bundle's OWN price — <see cref="Combo.PriceKopecks"/> — not the sum of its
/// slots. The sum is the à la carte reference the discount is measured against, and it is carried
/// here so the tile can show the saving. There is no second number to fall out of step when a dish
/// price changes; the reference moves with the dishes, the price does not.
/// <para>
/// Availability is the sale-side rule and not a copy of it: a bundle is sellable when every slot's
/// dish is on the shelf, or its declared substitute is. The authoritative answer is
/// <see cref="IComboService.ResolveSaleCompositionsAsync"/>, which is called again on the tap — this
/// is a tile hint so the cashier is not led into a refusal, not a second verdict that could disagree.
/// </para>
/// </remarks>
public sealed class MenuComboViewModel : ObservableObject
{
    private string priceText;
    private string compositionText;
    private string discountText = string.Empty;

    public MenuComboViewModel(
        Combo combo,
        long priceKopecks,
        string? blockedDishName,
        string priceText,
        string compositionText)
    {
        Id = combo.Id;
        Name = combo.Name;
        PriceKopecks = priceKopecks;
        // The DISH's price, not the slot's stored override — same reason as
        // ComboFormViewModel.UnitKopecks and ComboRowViewModel.Line. This is the figure the customer
        // is quoted a saving against, so a number the operator can neither see nor change has no
        // business in it. Kept in step with the other three call sites on purpose: they were four
        // copies that could drift apart, which is exactly how a tile showed 12.28% while the form
        // showed 24.24% for the same bundle on the same screen.
        ReferenceKopecks = ComboPricing.ReferenceKopecks(
            combo.Components.Select(slot => new ComboComponentPrice(
                slot.QuantityPerUnit,
                slot.Product?.PriceKopecks ?? 0)));
        DiscountText = DescribeDiscount(priceKopecks, ReferenceKopecks);
        this.blockedDishName = blockedDishName;
        this.priceText = priceText;
        this.compositionText = compositionText;
    }

    public Guid Id { get; }

    /// <summary>
    /// The bundle's name. Assigned once and never rewritten: it is the tile's identity, and a bundle
    /// whose name changed while the row was on screen is a catalogue edit the cashier did not make.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// What is in the bundle, as one line of dish names — «Американо, Чизкейк». Empty for a bundle whose
    /// slots point at rows that no longer exist, which is why the tile binds it through
    /// <c>IsPresent</c>: a line of nothing reads as a tile that failed to load.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Name"/> this one IS rewritten on a reload, because a bundle's composition is
    /// what a cashier edits in the catalogue and they will be looking for their change on the menu
    /// board straight afterwards. See <see cref="ComboComposition"/> for the truncation rule and
    /// <see cref="Refresh"/> for why it has to travel onto the instance already in the collection.
    /// </remarks>
    public string CompositionText
    {
        get => compositionText;
        private set => SetProperty(ref compositionText, value);
    }

    /// <summary>The bundle's own price, in kopecks. What the till charges.</summary>
    public long PriceKopecks
    {
        get => priceKopecks;
        private set => SetProperty(ref priceKopecks, value);
    }

    /// <summary>
    /// The à la carte sum of the slots, in kopecks. A REFERENCE, not a price — it is what the
    /// discount is measured against. Recomputed on every load from the freshly read components.
    /// </summary>
    public long ReferenceKopecks
    {
        get => referenceKopecks;
        private set => SetProperty(ref referenceKopecks, value);
    }

    /// <summary>
    /// What the price works out to against the à la carte sum, in words: «Скидка 19%»,
    /// «Наценка 5%», or empty when there is nothing to compare against.
    /// </summary>
    public string DiscountText
    {
        get => discountText;
        private set => SetProperty(ref discountText, value);
    }

    private long priceKopecks;
    private long referenceKopecks;
    private string? blockedDishName;

    /// <summary>The dish that makes this bundle unsellable, or <c>null</c> when every slot resolves.</summary>
    public string? BlockedDishName
    {
        get => blockedDishName;
        private set
        {
            if (SetProperty(ref blockedDishName, value)) OnPropertyChanged(nameof(UnavailableText));
        }
    }

    /// <summary>False when a slot has neither its dish nor a usable substitute.</summary>
    public bool IsAvailable => BlockedDishName is null;

    /// <summary>The bundle's own price, in the active currency.</summary>
    public string PriceText
    {
        get => priceText;
        private set => SetProperty(ref priceText, value);
    }

    /// <summary>
    /// Why the tile is dimmed. Names the dish: "нет в наличии" with no name sends the cashier
    /// through four slots to find which one it is, and the sale-side refusal says the same thing.
    /// </summary>
    public string UnavailableText => BlockedDishName is null
        ? string.Empty
        : $"нет в наличии: {BlockedDishName}";

    /// <summary>
    /// What the bundle's own price works out to against the à la carte sum, in words. Cheaper is a
    /// discount, dearer is a surcharge (labelled honestly, never as a negative discount), equal says
    /// so plainly, and a zero reference has no percentage at all.
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

    /// <summary>
    /// Re-raises the price figure after the operator's currency changed on the Settings tab.
    /// </summary>
    /// <remarks>
    /// The conversion figure is baked into the tile at load time, because it is
    /// <see cref="Combo.PriceKopecks"/> from the catalogue rather than a property of a
    /// <see cref="Product"/> a converter could read — so unlike the product tiles, nothing re-raises
    /// this on its own when <c>Currencies.Default</c> changes under the still-alive ViewModel.
    /// </remarks>
    public void RefreshMoneyText() => OnPropertyChanged(nameof(PriceText));

    /// <summary>
    /// Takes the figures off a freshly loaded tile.
    /// </summary>
    /// <remarks>
    /// The tiles are refreshed rather than re-created on every load, so this is what carries the new
    /// values onto the instance already in the collection — a row replaced on each return to the tab
    /// would make the whole bundle strip flicker for what is usually no change at all, and would
    /// drop the tile the cashier's finger is on top of.
    /// <para>
    /// <see cref="CompositionText"/> goes through its setter rather than being assigned like
    /// <see cref="PriceText"/>, so the common case — a composition nobody edited — announces nothing
    /// at all, which is the same reason the other two announce themselves outright when the value is
    /// identical.
    /// </para>
    /// </remarks>
    public void Refresh(MenuComboViewModel incoming)
    {
        PriceKopecks = incoming.PriceKopecks;
        ReferenceKopecks = incoming.ReferenceKopecks;
        discountText = incoming.discountText;
        priceText = incoming.priceText;
        CompositionText = incoming.compositionText;
        BlockedDishName = incoming.blockedDishName;

        // SetProperty raised nothing for the price because the value is identical (the common case,
        // since the composition and the currency rarely both moved), so it is announced outright.
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(DiscountText));
    }}
