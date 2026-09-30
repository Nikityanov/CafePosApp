using System.Collections.ObjectModel;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

public partial class CartItemViewModel : ObservableObject
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal Price { get; init; }
    public string? SelectedModifierName { get; init; }
    public string? SelectedVariantName { get; init; }

    private int quantity;

    /// <summary>
    /// Raises <see cref="LineTotal"/> as well. The cart row binds that computed value, and it used
    /// to be refreshed only where a caller remembered to invoke NotifyLineTotalChanged().
    /// </summary>
    public int Quantity
    {
        get => quantity;
        set { if (SetProperty(ref quantity, value)) OnPropertyChanged(nameof(LineTotal)); }
    }

    public decimal LineTotal => Price * Quantity;

    public CheckoutLine ToCheckoutLine() => new(ProductId, ProductName, Price, Quantity, SelectedModifierName, SelectedVariantName);

    public static CartItemViewModel FromLine(CheckoutLine line) => new()
    {
        ProductId = line.ProductId,
        ProductName = line.ProductName,
        Price = line.Price,
        SelectedModifierName = line.ModifierName,
        SelectedVariantName = line.VariantName,
        Quantity = line.Quantity
    };
}

/// <summary>
/// A category chip on the menu screen. <see cref="Category"/> is null for the synthetic
/// "Все" chip, which is part of <see cref="MenuViewModel.Categories"/> so that the whole
/// strip is a single scrolling list (it used to be a lone Button next to a CollectionView).
/// </summary>
public sealed class CategoryMenuItemViewModel : ObservableObject
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb("#E3F2FD"),
        Color.FromArgb("#E8F5E9"),
        Color.FromArgb("#FFF3E0"),
        Color.FromArgb("#F3E5F5"),
        Color.FromArgb("#FFEBEE"),
        Color.FromArgb("#E0F7FA"),
        Color.FromArgb("#FFF8E1"),
        Color.FromArgb("#E8EAF6"),
    ];

    /// <summary>Neutral background of the "Все" chip — no category colour, so grey.</summary>
    private static readonly Color AllChipColor = Color.FromArgb("#E4E4E4");

    /// <summary>
    /// The palette is pastel, so an unselected label stays dark in both themes. Relying on the
    /// implicit Button TextColor (white in the light theme) rendered white text on a near-white
    /// chip, i.e. an unreadable category strip.
    /// </summary>
    private static readonly Color ChipForeground = Color.FromArgb("#1A1A1A");

    private static readonly Color SelectedForeground = Colors.White;

    /// <summary>Selected chip fill. Mirrors the Primary token in Resources/Styles/Colors.xaml.</summary>
    private static readonly Color SelectedBackground = Color.FromArgb("#512BD4");

    /// <summary>Sort key of the "Все" chip. Real category ids are generated Guids.</summary>
    public static readonly Guid AllKey = Guid.Empty;

    /// <summary>
    /// The "no filter" chip; always the first item of the strip. A factory rather than a shared
    /// instance: the chip carries mutable selection state, and ViewModels are registered transient.
    /// </summary>
    public static CategoryMenuItemViewModel CreateAll() => new(null, 0, isAll: true);

    private CategoryMenuItemViewModel(Category? category, int index, bool isAll)
    {
        Category = category;
        IsAll = isAll;
        ChipColor = isAll ? AllChipColor : Palette[index % Palette.Length];
        IsSelected = isAll;
    }

    public CategoryMenuItemViewModel(Category category, int index)
        : this(category, index, isAll: false)
    {
    }

    public Category? Category { get; }

    /// <summary>True for the synthetic "Все" chip, whose <see cref="Category"/> is null.</summary>
    public bool IsAll { get; }

    public string Name => IsAll ? "Все" : Category!.Name;

    public Color ChipColor { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            // Both the fill and the label depend on the selection, so both must be announced:
            // only ChipBackground would leave dark text on the selected purple fill.
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(ChipBackground));
                OnPropertyChanged(nameof(ChipTextColor));
            }
        }
    }

    /// <summary>Bound to the chip background; the selection tint wins over the palette colour.</summary>
    public Color ChipBackground => IsSelected ? SelectedBackground : ChipColor;

    public Color ChipTextColor => IsSelected ? SelectedForeground : ChipForeground;

    /// <summary>Stable identity for <see cref="MenuViewModel.Categories"/> diffing.</summary>
    public Guid Key => IsAll ? AllKey : Category!.Id;
}
