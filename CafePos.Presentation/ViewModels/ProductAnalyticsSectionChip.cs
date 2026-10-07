using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>One chip of the section filter strip: «Все» plus one chip per section present in the shift.</summary>
/// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

public sealed partial class ProductAnalyticsSectionChip : ObservableObject
{
    public ProductAnalyticsSectionChip(Guid? sectionId, string name)
    {
        SectionId = sectionId;
        Name = name;
    }

    /// <summary>The section this chip selects, or null for «Без раздела».</summary>
    public Guid? SectionId { get; }

    public string Name { get; }

    private int count;
    public int Count
    {
        get => count;
        set
        {
            if (SetProperty(ref count, value)) OnPropertyChanged(nameof(Label));
        }
    }

    private decimal revenue;
    public decimal Revenue
    {
        get => revenue;
        set
        {
            if (SetProperty(ref revenue, value)) OnPropertyChanged(nameof(Share));
        }
    }

    /// <summary>«34% выручки смены». Empty at zero, where a share would only add a division.</summary>
    public string Share
    {
        get
        {
            var share = analyticsRevenueShare;
            return share <= 0 ? string.Empty : $"{Math.Round(share * 100)}% выручки смены";
        }
    }

    private double analyticsRevenueShare;

    /// <summary>Recomputed from the shift total after every load, because the denominator moves.</summary>
    public void SetShare(double share)
    {
        if (Math.Abs(analyticsRevenueShare - share) < 0.0001) return;
        analyticsRevenueShare = share;
        OnPropertyChanged(nameof(Share));
    }

    /// <summary>The name and the count. A zero count stays, because «Напитки · 0» is an answer.</summary>
    public string Label => Count > 0 ? $"{Name} · {Count}" : Name;

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    /// <summary>What a screen reader says for the whole chip, including the share.</summary>
    public string Hint => IsSelected ? $"{Label}. Выбран" : Label;
}

