using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One choice of the grouping control.</summary>
public sealed partial class ProductAnalyticsGroupingOption : ObservableObject
{
    public ProductAnalyticsGroupingOption(ProductAnalyticsGrouping grouping, string name)
    {
        Grouping = grouping;
        Name = name;
    }

    public ProductAnalyticsGrouping Grouping { get; }

    public string Name { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!SetProperty(ref isSelected, value)) OnPropertyChanged(nameof(Hint));
        }
    }

    public string Hint => $"Группировать: {Name}{(IsSelected ? ". Выбрано" : string.Empty)}";
}

