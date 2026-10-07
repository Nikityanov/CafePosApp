using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One dish header with its modifier rows under it, collapsible.</summary>
/// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

public sealed partial class ProductAnalyticsDishRow : ObservableObject
{
    public ProductAnalyticsDishRow(ProductAnalyticsDish dish, string share)
    {
        Name = dish.Name;
        Quantity = dish.Quantity;
        Revenue = dish.Revenue;
        Share = share;
        Lines = new ObservableCollection<ProductAnalyticsLineRow>(dish.Lines.Select(line => new ProductAnalyticsLineRow(line)));
    }

    public string Name { get; }

    public int Quantity { get; }

    public decimal Revenue { get; }

    /// <summary>«12% выручки смены», or empty at zero. Resolved at build — the denominator is fixed.</summary>
    public string Share { get; }

    public ObservableCollection<ProductAnalyticsLineRow> Lines { get; }

    private bool isExpanded = true;

    /// <summary>Whether the modifier lines are shown.</summary>
    /// <remarks>Почему так - `docs/decisions/combos.md`</remarks>

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!SetProperty(ref isExpanded, value)) return;
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(ChevronGlyph));
            OnPropertyChanged(nameof(Hint));
        }
    }

    /// <summary>The chevron's state, spelled out because a glyph is not a state.</summary>
    public bool IsCollapsed => !isExpanded;

    /// <summary>An up chevron while open, a right one when collapsed. A text glyph, not an asset.</summary>
    public string ChevronGlyph => isExpanded ? "⌃" : "›";

    public string QuantityText => $"× {Quantity}";

    public string RevenueText => TextFormat.Money(Revenue);

    /// <summary>The header as one sentence for a screen reader: what it is, how much, and whether it is open.</summary>
    /// <remarks>Почему так - `docs/decisions/combos.md`</remarks>

    public string Hint => $"{Name}. {QuantityText}, {RevenueText}. {Share}. {(isExpanded ? "Развёрнуто" : "Свёрнуто")}";
}

