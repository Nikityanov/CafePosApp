using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One modifier line of the product breakdown, as it is bound on screen.</summary>
/// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

public sealed partial class ProductAnalyticsLineRow : ObservableObject
{
    private bool isVisible = true;

    public ProductAnalyticsLineRow(ProductAnalyticsLine line)
    {
        ProductName = line.DishName;
        ModifierName = line.ModifierName;
        Quantity = line.Quantity;
        RevenueText = TextFormat.Money(line.Revenue);
        Hint = $"{line.DishName}, {line.ModifierName}: × {line.Quantity}, {RevenueText}";
    }

    public string ProductName { get; }

    /// <summary>The composed modifier/variant description, or «Без модификатора».</summary>
    public string ModifierName { get; }

    public int Quantity { get; }

    /// <summary>Money in the active currency, resolved when the row was built.</summary>
    public string RevenueText { get; }

    /// <summary>The whole line in one sentence, for a screen reader that cannot see the columns.</summary>
    public string Hint { get; }

    public bool IsVisible
    {
        get => isVisible;
        set => SetProperty(ref isVisible, value);
    }
}

