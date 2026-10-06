using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

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

public partial class CartItemViewModel : ObservableObject
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;

    /// <summary>
    /// The price this line was ALLOWED to be sold at — the catalogue sum over its components, decided
    /// before anything was typed. Immutable for the life of the line, exactly as
    /// <see cref="OrderItem.ListPriceKopecks"/> is; the one thing that rewrites it here is a change of
    /// composition, which changes what the line IS rather than what it costs.
    /// <para>
    /// It exists on the cart row so the cashier can see the override while they are standing at the
    /// till. <see cref="Price"/> is what is charged and may be changed by hand; the two differing IS
    /// the override, and it is what the shift report's discount section will show afterwards. Nothing
    /// is asked for at the till — a reason typed there becomes the first value anyone ever picks and
    /// adds no control — so this row is the whole feedback loop.
    /// </para>
    /// </summary>
    public decimal ListPrice
    {
        get => listPrice;
        set
        {
            if (!SetProperty(ref listPrice, value)) return;
            OnPropertyChanged(nameof(ListPriceText));
            OnPropertyChanged(nameof(IsPriceOverridden));
        }
    }

    private decimal listPrice;
    private decimal price;

    /// <summary>What the line is charged per unit. Editable by hand; see <see cref="ListPrice"/>.</summary>
    public decimal Price
    {
        get => price;
        set
        {
            if (!SetProperty(ref price, value)) return;
            OnPropertyChanged(nameof(PriceText));
            OnPropertyChanged(nameof(IsPriceOverridden));
        }
    }

    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    /// <summary>
    /// The bundle's composition, empty for an ordinary dish — which is what makes "is this a combo"
    /// answerable without a flag. Rendered indented under the line, and part of the merge key so two
    /// different builds of one bundle stay two lines.
    /// </summary>
    public ObservableCollection<LineComponentViewModel> Components { get; } = [];

    /// <summary>True for a bundle: a line with a composition the cashier can open and change.</summary>
    public bool IsCombo => Components.Count > 0;

    /// <summary>
    /// Whether the charged price differs from the allowed one. The row shows the allowed price struck
    /// through beside the changed one — Bitta's "was …" badge, which is at once the deterrent and the
    /// evidence, and costs nothing at the till.
    /// </summary>
    public bool IsPriceOverridden => Price != ListPrice;

    /// <summary>The allowed price, formatted — printed struck through next to <see cref="PriceText"/>.</summary>
    public string ListPriceText => TextFormat.Money(ListPrice);

    /// <summary>The charged unit price in the active currency, e.g. "180,00 ₽". Tapping it re-prices.</summary>
    public string PriceText => TextFormat.Money(Price);

    private int quantity;

    /// <summary>
    /// Raises <see cref="LineTotal"/> as well. The cart row binds that computed value, and it used
    /// to be refreshed only where a caller remembered to invoke NotifyLineTotalChanged().
    /// </summary>
    public int Quantity
    {
        get => quantity;
        set
        {
            if (!SetProperty(ref quantity, value)) return;
            OnPropertyChanged(nameof(LineTotal));
            // The cart row binds the formatted amount, so it has to be raised with the decimal.
            OnPropertyChanged(nameof(LineTotalText));
        }
    }

    public decimal LineTotal => Price * Quantity;

    /// <summary>
    /// The line total in the active currency, e.g. "220,00 ₿". NOT bound by the cart row: the row
    /// shows the unit price and the quantity, and a total beside them was the same figure twice at
    /// quantity 1 and the first multiplied by a number already on screen at any other quantity. Kept
    /// because <see cref="MenuViewModel.Total"/> is Σ over the decimal <see cref="LineTotal"/> and
    /// this is its formatted form.
    /// </summary>
    public string LineTotalText => TextFormat.Money(LineTotal);

    /// <summary>
    /// Re-raises <see cref="LineTotalText"/> without touching <see cref="Quantity"/>.
    /// </summary>
    /// <remarks>
    /// Needed because the formatted amount depends on the operator's currency setting, which can
    /// change while this row is still on screen — the cart is not rebuilt when it does, so the row
    /// would otherwise keep printing the previous sign. See MenuViewModel.RefreshMoneyText.
    /// </remarks>
    public void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ListPriceText));
    }

    /// <summary>
    /// Whether a tapped dish joins this line or opens a new one.
    /// </summary>
    /// <remarks>
    /// Through <see cref="OrderLineKey"/> and not an inline comparison, because the composition is
    /// part of the identity: two builds of the same bundle are two lines at two prices, and one
    /// bundle added twice is one line at quantity 2. The comparison this replaced left the
    /// composition out entirely, which merged both cases — two different bundles into one line, and
    /// one bundle into two.
    /// </remarks>
    public string MergeKey => OrderLineKey.For(
        ProductId,
        SelectedModifierName,
        SelectedVariantName,
        Components.Select(component => (component.ProductId, component.QuantityPerUnit)));

    public CheckoutLine ToCheckoutLine() => new(
        ProductId,
        ProductName,
        Price,
        Quantity,
        SelectedModifierName,
        SelectedVariantName,
        Components.Count == 0
            ? null
            : Components.Select(component => new CheckoutComponent(
                component.ProductId,
                component.Name,
                component.QuantityPerUnit,
                component.UnitKopecks,
                component.ReferenceKopecks)).ToList());

    public static CartItemViewModel FromLine(CheckoutLine line) => new()
    {
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        Price = line.Price,
        // A restored line has no remembered "allowed" price, so the charged price stands in as both.
        // That is the honest default for a draft: nothing has been overridden on it yet, and showing
        // a phantom strikethrough because the column arrived empty would be worse than showing none.
        ListPrice = line.Price,
        SelectedModifierName = line.ModifierName,
        SelectedVariantName = line.VariantName,
        Quantity = line.Quantity
    };

    /// <summary>
    /// Fills <see cref="Components"/> from a cart line's composition. Separate from
    /// <see cref="FromLine"/> because the collection is read-only initialised: a combo restored from
    /// the parking lot arrives with slots, and dropping them would leave a bundle on the cart
    /// printing as one line with nothing under it — and, worse, with an empty merge signature, so it
    /// would merge with an identical bundle and split from itself.
    /// </summary>
    public CartItemViewModel WithComponents(IReadOnlyList<CheckoutComponent>? components)
    {
        foreach (var component in LineComponentViewModel.FromLine(components)) Components.Add(component);
        OnCompositionChanged();
        return this;
    }

    /// <summary>
    /// Re-announces everything that depends on the composition having changed.
    /// </summary>
    /// <remarks>
    /// <see cref="Components"/> is an <see cref="ObservableCollection{T}"/>, so the rows themselves
    /// update on their own — but <see cref="IsCombo"/>, which gates the tap target that opens the
    /// editor, is computed from the collection's count and would otherwise keep reading the state
    /// from before the slots were written.
    /// </remarks>
    public void OnCompositionChanged() => OnPropertyChanged(nameof(IsCombo));
}

/// <summary>
/// What a bundle tile prints about what is INSIDE the bundle: one line of dish names, cut to a budget
/// and closed with a count of what did not fit.
/// </summary>
/// <remarks>
/// <b>WHY THE TILE NEEDS THIS AT ALL.</b> A bundle printed only its name and its price — «coffe and
/// more — 350,00 ₽» — so the only way to learn what was in it was to open the composition sheet. That
/// answers no question a customer actually asks. «Что входит?» is asked at the counter, with the
/// customer standing there, and it is answered from the tile.
/// <para>
/// <b>ONE LINE, A BUDGET, AND A COUNT.</b> The tile has a fixed height and a fixed width, and a bundle
/// whose composition runs long must not grow it: «Эспрессо, Круассан, Чизкейк, Мин. вода» on a tile that
/// sized itself to it would push every other tile out of shape and make the strip unscannable, which is
/// the one thing a menu board may not be. So names are dropped from the end and the rest is named
/// outright — «Эспрессо, Круассан, и ещё 2» — rather than silently clipped mid-word. The budget is in
/// CHARACTERS and not in names, because a bundle of «Чай» and a bundle of «Капучино с корицей» are not
/// the same width and a count-based rule would fit two of the first and half of the second.
/// </para>
/// <para>
/// <b>NO PRICES, AND NO MULTIPLIER UNLESS IT IS NOT 1.</b> The operator naming a dish needs identity,
/// not arithmetic: the bundle's price is already on the tile and the per-dish price is in the sheet,
/// and a tile that repeated the arithmetic would be a receipt, not a menu. The multiplicity is
/// different — «2 × Круассан» and «Круассан» are different bundles — so it is printed when it is above
/// 1 and omitted otherwise.
/// </para>
/// <para>
/// <b>THE SLOT'S OWN DISH, NOT WHAT WILL ACTUALLY BE SOLD FOR IT.</b> A slot whose dish has run out is
/// being sold as its declared substitute, and the editor sheet names that substitution because the
/// cashier has to decide about it. The tile is not where that decision is made, the substituted name
/// is twice as long, and what a customer standing at the till is asking about is what the bundle
/// <i>is</i>. The tile also already carries the stock state that does change the answer — it dims and
/// names the dish outright when the bundle cannot be sold at all.
/// </para>
/// <para>
/// The whole thing is one pass over the components the tile was built from. <c>GetCombosAsync</c>
/// already loads every slot with its <see cref="Product"/> attached (see <c>ComboService.LoadAsync</c>),
/// so this costs no query at all — the names were on hand to compute the price and are now read twice.
/// </para>
/// </remarks>
internal static class ComboComposition
{
    /// <summary>
    /// How many characters the tile's composition line may use. GONE WITH THE 208dp TILE.
    /// <para>
    /// The tile is full width and wraps now, so there is no budget to keep and no «и ещё N» tail to
    /// measure. A character budget here existed only because a half-width card had to fit the
    /// composition on one line, and it produced exactly the wrong thing: a bundle the cashier could
    /// not read.
    /// </para>
    /// </summary>
    [Obsolete("The combo tile is full width and wraps; there is no character budget. Kept only to name what was removed and why.", false)]
    internal const int TileCharBudget = 24;

    /// <summary>
    /// The slots in the order a bundle reads in.
    /// </summary>
    /// <remarks>
    /// Ordered HERE and not off the loaded collection, because <c>ComboComponent</c> carries no
    /// <c>SortOrder</c> and the query behind it applies no <c>ORDER BY</c>: the collection's order is
    /// whatever the join produced. This is the one place the order is defined, and both the tile and
    /// the composition sheet go through it, so the two cannot disagree about what the first dish in a
    /// bundle is.
    /// </remarks>
    internal static IOrderedEnumerable<ComboComponent> CatalogueOrder(IEnumerable<ComboComponent> components) =>
        components
            .OrderBy(component => component.Product?.Name, StringComparer.CurrentCulture)
            .ThenBy(component => component.ProductId);

    /// <summary>The slots as the tile prints them: every name, in catalogue order.</summary>
    /// <remarks>
    /// NO LONGER TRUNCATED, and that is the owner's decision. The tile used to be a 208dp card in a
    /// horizontal strip, which forced a character budget and printed «Американо, и ещё 2» — a bundle
    /// whose contents the operator could not read, on the one screen whose purpose is naming dishes to
    /// a customer standing at the counter. The tile is full width now and wraps, so every component is
    /// printed. A hidden dish is a worse defect than a taller page.
    /// </remarks>
    internal static string Summarise(IEnumerable<ComboComponent> components)
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

/// <summary>
/// A category chip on the menu screen. <see cref="Category"/> is null for the synthetic
/// "Все" chip, which is part of <see cref="MenuViewModel.Categories"/> so that the whole
/// strip is a single scrolling list (it used to be a lone Button next to a CollectionView).
/// </summary>
public sealed class CategoryMenuItemViewModel : ObservableObject
{
    /// <summary>
    /// Keys of the per-category pastel fills, in Resources/Styles/Colors.xaml.
    /// </summary>
    /// <remarks>
    /// This used to be an inline <c>Color[]</c> of eight <c>Color.FromArgb</c> literals — a private
    /// copy of the Category1..Category8 tokens. That is the defect a colour produced in C# always
    /// has: <c>AppThemeBinding</c> governs values that come from XAML, so a literal here is
    /// permanently light-themed, and the palette could be retuned in Colors.xaml with this file
    /// silently keeping the old values. Reading the same tokens by key means there is one source
    /// of truth. A light/dark PAIR is not used here on purpose: a category tint is
    /// a light pastel by design in BOTH themes, so there is no dark variant to switch to and no
    /// lookup of a second key that does not exist — passing the same key twice says exactly that.
    /// <para>
    /// The lookups go through <see cref="PaletteAccess"/> rather than MAUI's application resources
    /// directly: the ViewModels now live in CafePos.Presentation, which references only
    /// Microsoft.Maui.Graphics and so cannot see <c>Application.Current</c>. The palette itself is
    /// still the single definition in Colors.xaml — <c>MauiPalette</c> reads that same dictionary,
    /// so nothing is duplicated here.
    /// </para>
    /// </remarks>
    private static readonly string[] CategoryColorKeys =
        ["Category1", "Category2", "Category3", "Category4", "Category5", "Category6", "Category7", "Category8"];

    /// <summary>
    /// The pastel fills, read once from the palette. A key that is missing degrades to a neutral
    /// tone rather than throwing from a type initialiser, so a palette rename cannot stop the menu
    /// from loading.
    /// </summary>
    private static readonly Color[] Palette = ReadPalette();

    private static Color[] ReadPalette() =>
    [
        ..CategoryColorKeys.Select(key => PaletteAccess.Resolve(key, key))
    ];

    /// <summary>
    /// Neutral background of the "Все" chip — no category colour, so grey.
    /// </summary>
    /// <remarks>
    /// This was a literal <c>#E4E4E4</c>, a grey that exists in NO token in Colors.xaml: it sits
    /// between Gray100 #F5F5F5 and Gray200 #EEEEEE and matches neither, so it could not be moved
    /// into the palette without inventing a value. Gray100 #F5F5F5 is used here instead — it is a
    /// real tonal step, one notch lighter than Gray200 which is the fill the neutral
    /// <c>Tag</c> pills use, so the "no filter" chip reads as a very slightly darker neutral than
    /// the chrome around it and cannot be mistaken for a category tint.
    /// <para>
    /// It stays the SAME tone in dark mode. The chip carries a dark label on every unselected
    /// state (see <see cref="ChipForeground"/>), and darkening the fill would only shrink that
    /// contrast; the selected fill is what signals dark mode.
    /// </para>
    /// </remarks>
    private static Color AllChipColor =>
        PaletteAccess.Resolve("Gray100", "Gray100");

    /// <summary>
    /// Fill of the "Комбо" chip. Its own token in Colors.xaml, not a slot in
    /// <see cref="CategoryColorKeys"/>: a bundle is not a section, and borrowing a category pastel
    /// would say it was one. The value sits between Category4 and Category8, close enough to the
    /// family to belong on the strip and distinct enough at 13sp to be told apart.
    /// </summary>
    private static Color ComboChipColor =>
        PaletteAccess.Resolve("Combo", "Combo");

    /// <summary>
    /// The label on an unselected chip.
    /// </summary>
    /// <remarks>
    /// The palette is pastel, so an unselected label stays dark in both themes. Relying on the
    /// implicit Button TextColor (white in the light theme) rendered white text on a near-white
    /// chip, i.e. an unreadable category strip. The value was <c>#1A1A1A</c>, which is also in no
    /// token — Gray900 is #212121 and Black is #000000, and the chip label needs to clear 4.5:1
    /// against the darkest pastel in the strip (#E8F5E9), which both clear comfortably. Gray900 is
    /// used here so the label is a real tonal step rather than an unregistered near-black.
    /// </remarks>
    private static Color ChipForeground =>
        PaletteAccess.Resolve("Gray900", "Gray900");

    private static Color SelectedForeground =>
        PaletteAccess.Resolve("White", "PrimaryDarkText");

    /// <summary>Selected chip fill — the Primary pair, so the selection follows the theme.</summary>
    private static Color SelectedBackground =>
        PaletteAccess.Resolve("Primary", "PrimaryDark");

    /// <summary>Sort key of the "Все" chip. Real category ids are generated Guids.</summary>
    public static readonly Guid AllKey = Guid.Empty;

    /// <summary>
    /// Sort key of the "Комбо" chip. A fixed literal, not a random Guid, so it is stable across
    /// processes — the strip is rebuilt and diffed on every return to the tab, and a key that
    /// changed per launch would make <c>SyncWith</c> treat the chip as a different item each time
    /// and lose the selection highlight.
    /// </summary>
    public static readonly Guid CombosKey = Guid.Parse("00000000-0000-0000-0000-0000000000c0");

    /// <summary>
    /// The "no filter" chip; always the first item of the strip. A factory rather than a shared
    /// instance: the chip carries mutable selection state, and ViewModels are registered transient.
    /// </summary>
    public static CategoryMenuItemViewModel CreateAll() => new(null, 0, isAll: true);

    /// <summary>
    /// The "bundles only" chip, last in the strip. A pseudo-chip like "Все": a bundle carries no
    /// CategoryId, so it can never appear in the category list — but an operator who sells mostly
    /// combos still had no way to reach a screen that showed only them. It empties the product grid
    /// and leaves the bundle row as the only content, which is what "filter to combos" means when
    /// the two kinds live in separate collections.
    /// </summary>
    public static CategoryMenuItemViewModel CreateCombos() => new(null, 0, isAll: false, isCombos: true);

    private CategoryMenuItemViewModel(Category? category, int index, bool isAll, bool isCombos = false)
    {
        Category = category;
        IsAll = isAll;
        IsCombos = isCombos;
        ChipColor = isCombos ? ComboChipColor : isAll ? AllChipColor : Palette[index % Palette.Length];
        IsSelected = isAll;
    }

    public CategoryMenuItemViewModel(Category category, int index)
        : this(category, index, isAll: false)
    {
    }

    public Category? Category { get; }

    /// <summary>True for the synthetic "Все" chip, whose <see cref="Category"/> is null.</summary>
    public bool IsAll { get; }

    /// <summary>True for the synthetic "Комбо" chip. Like <see cref="IsAll"/>, Category is null.</summary>
    public bool IsCombos { get; }

    public string Name => IsAll ? "Все" : IsCombos ? "Комбо" : Category!.Name;

    public Color ChipColor { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            // Both the fill and the label depend on the selection, so both must be announced:
            // only ChipBackground would leave dark text on the selected purple fill.
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(ChipBackground));
                OnPropertyChanged(nameof(ChipTextColor));
            }
        }
    }

    /// <summary>Bound to the chip background; the selection tint wins over the palette colour.</summary>
    public Color ChipBackground => IsSelected ? SelectedBackground : ChipColor;

    public Color ChipTextColor => IsSelected ? SelectedForeground : ChipForeground;

    /// <summary>Stable identity for <see cref="MenuViewModel.Categories"/> diffing.</summary>
    public Guid Key => IsAll ? AllKey : IsCombos ? CombosKey : Category!.Id;
}
