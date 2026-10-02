using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePosApp.ViewModels;

/// <summary>
/// Which part of the orders board a filter chip shows. <see cref="All"/> keeps the default
/// two-section list; the other two narrow it to a single section.
/// </summary>
public enum OrderSectionFilter
{
    All,
    Preparing,
    Ready
}

/// <summary>
/// One chip of the orders-board section filter: "Все", "Готовятся" or "Ждут выдачи".
/// </summary>
/// <remarks>
/// The same pattern as <see cref="CategoryFilterChip"/> on the catalogue page, so the two filter
/// strips look and behave alike. The count is on the chip because the reason to filter is
/// arithmetic — an operator wants to know whether anything is waiting before they decide which
/// half of the board to look at.
/// </remarks>
public sealed partial class OrderFilterChip : ObservableObject
{
    public OrderFilterChip(OrderSectionFilter filter, string name)
    {
        Filter = filter;
        Name = name;
    }

    /// <summary>The section this chip filters by.</summary>
    public OrderSectionFilter Filter { get; }

    public string Name { get; }

    private int count;
    public int Count
    {
        get => count;
        set
        {
            if (SetProperty(ref count, value))
            {
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    /// <summary>
    /// The chip caption, with the count when there is something to count. A zero is left off for
    /// "Все" only by virtue of there always being orders or none at all; the section chips keep
    /// their 0, because "Готовятся · 0" is the answer to "is anything cooking?".
    /// </summary>
    public string Label => Count > 0 ? $"{Name} · {Count}" : Name;

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }
}
