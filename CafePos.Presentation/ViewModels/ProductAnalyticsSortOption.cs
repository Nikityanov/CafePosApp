using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One choice of the sorting control: a criterion, spelled out.</summary>
public sealed partial class ProductAnalyticsSortOption : ObservableObject
{
    public ProductAnalyticsSortOption(ProductAnalyticsSortCriterion criterion, string name)
    {
        Criterion = criterion;
        Name = name;
    }

    public ProductAnalyticsSortCriterion Criterion { get; }

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

    public string Hint => $"Сортировать: {Name}{(IsSelected ? ". Выбрано" : string.Empty)}";
}

