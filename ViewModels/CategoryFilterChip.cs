using CafePos.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePosApp.ViewModels;

/// <summary>
/// One chip of the product-list category filter strip: either the synthetic "Все" row
/// (<see cref="Category"/> is null) or a real category.
/// </summary>
/// <remarks>
/// Replaces the old fixed-width <c>Picker</c>, which opened a blocking modal on Android and
/// could not show the active filter as part of the list header. The strip is the same pattern
/// the menu page already uses for its category chips.
/// </remarks>
public sealed partial class CategoryFilterChip : ObservableObject
{
    public CategoryFilterChip(Category? category, string name)
    {
        Category = category;
        Name = name;
    }

    /// <summary>The category this chip filters by, or null for "Все".</summary>
    public Category? Category { get; }

    public string Name { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}
