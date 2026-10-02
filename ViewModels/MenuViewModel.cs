using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Controls;
using CafePosApp.Converters;
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
        set
        {
            if (!SetProperty(ref quantity, value)) return;
            OnPropertyChanged(nameof(LineTotal));
            // The cart row binds the formatted amount, so it has to be raised with the decimal.
            OnPropertyChanged(nameof(LineTotalText));
        }
    }

    public decimal LineTotal => Price * Quantity;

    /// <summary>The line total in the active currency, e.g. "220,00 ₿". Bound by the cart row.</summary>
    public string LineTotalText => TextFormat.Money(LineTotal);

    /// <summary>
    /// Re-raises <see cref="LineTotalText"/> without touching <see cref="Quantity"/>.
    /// </summary>
    /// <remarks>
    /// Needed because the formatted amount depends on the operator's currency setting, which can
    /// change while this row is still on screen — the cart is not rebuilt when it does, so the row
    /// would otherwise keep printing the previous sign. See MenuViewModel.RefreshMoneyText.
    /// </remarks>
    public void RefreshMoneyText() => OnPropertyChanged(nameof(LineTotalText));

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
    /// <summary>
    /// Keys of the per-category pastel fills, in Resources/Styles/Colors.xaml.
    /// </summary>
    /// <remarks>
    /// This used to be an inline <c>Color[]</c> of eight <c>Color.FromArgb</c> literals — a private
    /// copy of the Category1..Category8 tokens. That is the defect a colour produced in C# always
    /// has: <c>AppThemeBinding</c> governs values that come from XAML, so a literal here is
    /// permanently light-themed, and the palette could be retuned in Colors.xaml with this file
    /// silently keeping the old values. Reading the same tokens by key means there is one source
    /// of truth. <see cref="ThemeColors.Resolve"/> is not needed here on purpose: a category tint is
    /// a light pastel by design in BOTH themes, so there is no dark variant to switch to and no
    /// lookup of a second key that does not exist.
    /// <para>
    /// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out
    /// of the write scope of the change that added it; it is a general theming helper, not a
    /// converter. ResourceStyles itself is the narrower dependency and is what is used below.
    /// </para>
    /// </remarks>
    private static readonly string[] CategoryColorKeys =
        ["Category1", "Category2", "Category3", "Category4", "Category5", "Category6", "Category7", "Category8"];

    /// <summary>
    /// The pastel fills, read from the palette. A key that is missing falls back to
    /// <see cref="Gray100"/> so a palette rename degrades to a neutral chip rather than throwing
    /// from a constructor.
    /// </summary>
    private static readonly Color[] Palette = ReadPalette();

    private static Color[] ReadPalette() =>
    [
        ..CategoryColorKeys.Select(key => ResourceStyles.TryGetColor(key) ?? ResourceStyles.TryGetColor("Gray100") ?? Colors.LightGray)
    ];

    /// <summary>
    /// Neutral background of the "Все" chip — no category colour, so grey.
    /// </summary>
    /// <remarks>
    /// This was a literal <c>#E4E4E4</c>, a grey that exists in NO token in Colors.xaml: it sits
    /// between Gray100 #F5F5F5 and Gray200 #EEEEEE and matches neither, so it could not be moved
    /// into the palette without inventing a value. Gray100 #F5F5F5 is used here instead — it is a
    /// real tonal step, one notch lighter than Gray200 which is the fill the neutral
    /// <c>Tag</c> pills use, so the "no filter" chip reads as a very slightly darker neutral than
    /// the chrome around it and cannot be mistaken for a category tint.
    /// <para>
    /// It stays the SAME tone in dark mode. The chip carries a dark label on every unselected
    /// state (see <see cref="ChipForeground"/>), and darkening the fill would only shrink that
    /// contrast; the selected fill is what signals dark mode.
    /// </para>
    /// </remarks>
    private static Color AllChipColor =>
        ResourceStyles.TryGetColor("Gray100") ?? Colors.LightGray;

    /// <summary>
    /// The label on an unselected chip.
    /// </summary>
    /// <remarks>
    /// The palette is pastel, so an unselected label stays dark in both themes. Relying on the
    /// implicit Button TextColor (white in the light theme) rendered white text on a near-white
    /// chip, i.e. an unreadable category strip. The value was <c>#1A1A1A</c>, which is also in no
    /// token — Gray900 is #212121 and Black is #000000, and the chip label needs to clear 4.5:1
    /// against the darkest pastel in the strip (#E8F5E9), which both clear comfortably. Gray900 is
    /// used here so the label is a real tonal step rather than an unregistered near-black.
    /// </remarks>
    private static Color ChipForeground =>
        ResourceStyles.TryGetColor("Gray900") ?? Colors.Black;

    private static Color SelectedForeground =>
        ThemeColors.Resolve("White", "PrimaryDarkText");

    /// <summary>Selected chip fill — the Primary pair, so the selection follows the theme.</summary>
    private static Color SelectedBackground =>
        ThemeColors.Resolve("Primary", "PrimaryDark");

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
