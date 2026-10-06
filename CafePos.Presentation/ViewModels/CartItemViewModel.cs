using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

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
