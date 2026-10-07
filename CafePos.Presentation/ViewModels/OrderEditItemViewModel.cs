using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

public partial class OrderEditItemViewModel : ObservableObject
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;

    /// <summary>The price this line was allowed to be sold at, read off the order's and never rewritten here.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public decimal ListPrice { get; init; }

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

    /// <summary>The allowed price formatted, printed struck through next to <see cref="PriceText"/>.</summary>
    public string ListPriceText => TextFormat.Money(ListPrice);

    /// <summary>The charged unit price in the active currency. Tapping it re-prices the line.</summary>
    public string PriceText => TextFormat.Money(Price);

    /// <summary>Whether the charged price differs from the allowed one — the override, visible at once.</summary>
    public bool IsPriceOverridden => Price != ListPrice;

    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    /// <summary>The bundle's composition, empty for an ordinary dish. Printed indented under the line, and part of the merge key so a save cannot rewrite one build of a bundle as another.</summary>

    public ObservableCollection<LineComponentViewModel> Components { get; } = [];

    /// <summary>True for a bundle: a line whose composition can be opened and changed.</summary>
    public bool IsCombo => Components.Count > 0;

    /// <summary>Whether this line and another are the same sale.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public string MergeKey => OrderLineKey.For(
        ProductId,
        SelectedModifierName,
        SelectedVariantName,
        Components.Select(component => (component.ProductId, component.QuantityPerUnit)));

    private int quantity;
    public int Quantity
    {
        get => quantity;
        set
        {
            if (!SetProperty(ref quantity, value)) return;
            OnPropertyChanged(nameof(LineTotal));
            // LineTotalText is NOT bound by the row any more: the row shows the unit price, which is what
            // re-pricing edits, and a total beside it was the same figure twice at quantity 1. It is
            // still raised because the decimal LineTotal is what the page's own Total sums.
            OnPropertyChanged(nameof(LineTotalText));
        }
    }

    public decimal LineTotal => Price * Quantity;

    /// <summary>The line total in the active currency, e.g. "320,00 ₿". Bound by the row.</summary>
    public string LineTotalText => TextFormat.Money(LineTotal);

    /// <summary>Re-raises the formatted amounts after the currency setting changed.</summary>
    public void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(LineTotalText));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ListPriceText));
    }

    /// <summary>Re-announces <see cref="IsCombo"/> after the composition changed.</summary>
    public void OnCompositionChanged() => OnPropertyChanged(nameof(IsCombo));

    /// <summary>The entity this row saves as.</summary>
    /// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

    public OrderItem ToOrderItem() => new()
    {
        Id = Guid.NewGuid(),
        ProductId = ProductId,
        ProductName = ProductName,
        Price = Price,
        ListPriceKopecks = Money.ToKopecks(ListPrice),
        Quantity = Quantity,
        SelectedModifierName = SelectedModifierName,
        SelectedVariantName = SelectedVariantName,
        Components = Components.Select((component, index) => new OrderItemComponent
        {
            Id = Guid.NewGuid(),
            ProductId = component.ProductId,
            ProductName = component.Name,
            QuantityPerUnit = component.QuantityPerUnit,
            UnitPriceKopecks = component.UnitKopecks,
            ReferencePriceKopecks = component.ReferenceKopecks,
            SortOrder = index
        }).ToList()
    };
}

