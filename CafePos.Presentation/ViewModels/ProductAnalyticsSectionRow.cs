using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One section header with the dishes sold in it.</summary>
public sealed partial class ProductAnalyticsSectionRow : ObservableObject
{
    public ProductAnalyticsSectionRow(ProductAnalyticsSection section, Func<decimal, string> share)
    {
        Name = section.Name;
        Quantity = section.Quantity;
        Revenue = section.Revenue;
        Share = share(section.Revenue);
        Dishes = new ObservableCollection<ProductAnalyticsDishRow>(
            section.Dishes.Select(dish => new ProductAnalyticsDishRow(dish, share(dish.Revenue))));
    }

    public string Name { get; }

    public int Quantity { get; }

    public decimal Revenue { get; }

    /// <summary>«12% выручки смены» for the section, or empty when the shift sold nothing.</summary>
    public string Share { get; }

    public ObservableCollection<ProductAnalyticsDishRow> Dishes { get; }

    public string QuantityText => $"× {Quantity}";

    public string RevenueText => TextFormat.Money(Revenue);

    public string Hint => $"{Name}. {QuantityText}, {RevenueText}. {Share}";
}

