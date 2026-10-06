using CafePos.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

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
    /// of truth. A light/dark PAIR is not used here on purpose: a category tint is
    /// a light pastel by design in BOTH themes, so there is no dark variant to switch to and no
    /// lookup of a second key that does not exist — passing the same key twice says exactly that.
    /// <para>
    /// The lookups go through <see cref="PaletteAccess"/> rather than MAUI's application resources
    /// directly: the ViewModels now live in CafePos.Presentation, which references only
    /// Microsoft.Maui.Graphics and so cannot see <c>Application.Current</c>. The palette itself is
    /// still the single definition in Colors.xaml — <c>MauiPalette</c> reads that same dictionary,
    /// so nothing is duplicated here.
    /// </para>
    /// </remarks>
    private static readonly string[] CategoryColorKeys =
        ["Category1", "Category2", "Category3", "Category4", "Category5", "Category6", "Category7", "Category8"];

    /// <summary>
    /// The pastel fills, read once from the palette and then cached.
    /// </summary>
    /// <remarks>
    /// <b>LAZILY, AND THAT IS NOT A MICRO-OPTIMISATION.</b> This used to be a
    /// <c>static readonly Color[] Palette = ReadPalette()</c>. A static field initialiser that throws
    /// poisons its type for the LIFETIME OF THE PROCESS: the CLR caches the
    /// <see cref="TypeInitializationException"/> and every later access re-throws it, even after the
    /// cause is fixed. The app happened to survive because the palette is registered before any
    /// ViewModel is built — but a test that constructed one first would have found a type that could
    /// never work again in that run, with an error pointing at the initialiser rather than at the
    /// missing registration. The first test to build a MenuViewModel did exactly that.
    /// </remarks>
    private static Color[]? palette;

    private static Color[] Palette => palette ??= ReadPalette();

    private static Color[] ReadPalette() =>
    [
        ..CategoryColorKeys.Select(key => PaletteAccess.Resolve(key, key))
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
        PaletteAccess.Resolve("Gray100", "Gray100");

    /// <summary>
    /// Fill of the "Комбо" chip. Its own token in Colors.xaml, not a slot in
    /// <see cref="CategoryColorKeys"/>: a bundle is not a section, and borrowing a category pastel
    /// would say it was one. The value sits between Category4 and Category8, close enough to the
    /// family to belong on the strip and distinct enough at 13sp to be told apart.
    /// </summary>
    private static Color ComboChipColor =>
        PaletteAccess.Resolve("Combo", "Combo");

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
        PaletteAccess.Resolve("Gray900", "Gray900");

    private static Color SelectedForeground =>
        PaletteAccess.Resolve("White", "PrimaryDarkText");

    /// <summary>Selected chip fill — the Primary pair, so the selection follows the theme.</summary>
    private static Color SelectedBackground =>
        PaletteAccess.Resolve("Primary", "PrimaryDark");

    /// <summary>Sort key of the "Все" chip. Real category ids are generated Guids.</summary>
    public static readonly Guid AllKey = Guid.Empty;

    /// <summary>
    /// Sort key of the "Комбо" chip. A fixed literal, not a random Guid, so it is stable across
    /// processes — the strip is rebuilt and diffed on every return to the tab, and a key that
    /// changed per launch would make <c>SyncWith</c> treat the chip as a different item each time
    /// and lose the selection highlight.
    /// </summary>
    public static readonly Guid CombosKey = Guid.Parse("00000000-0000-0000-0000-0000000000c0");

    /// <summary>
    /// The "no filter" chip; always the first item of the strip. A factory rather than a shared
    /// instance: the chip carries mutable selection state, and ViewModels are registered transient.
    /// </summary>
    public static CategoryMenuItemViewModel CreateAll() => new(null, 0, isAll: true);

    /// <summary>
    /// The "bundles only" chip, last in the strip. A pseudo-chip like "Все": a bundle carries no
    /// CategoryId, so it can never appear in the category list — but an operator who sells mostly
    /// combos still had no way to reach a screen that showed only them. It empties the product grid
    /// and leaves the bundle row as the only content, which is what "filter to combos" means when
    /// the two kinds live in separate collections.
    /// </summary>
    public static CategoryMenuItemViewModel CreateCombos() => new(null, 0, isAll: false, isCombos: true);

    private CategoryMenuItemViewModel(Category? category, int index, bool isAll, bool isCombos = false)
    {
        Category = category;
        IsAll = isAll;
        IsCombos = isCombos;
        ChipColor = isCombos ? ComboChipColor : isAll ? AllChipColor : Palette[index % Palette.Length];
        IsSelected = isAll;
    }

    public CategoryMenuItemViewModel(Category category, int index)
        : this(category, index, isAll: false)
    {
    }

    public Category? Category { get; }

    /// <summary>True for the synthetic "Все" chip, whose <see cref="Category"/> is null.</summary>
    public bool IsAll { get; }

    /// <summary>True for the synthetic "Комбо" chip. Like <see cref="IsAll"/>, Category is null.</summary>
    public bool IsCombos { get; }

    public string Name => IsAll ? "Все" : IsCombos ? "Комбо" : Category!.Name;

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
    public Guid Key => IsAll ? AllKey : IsCombos ? CombosKey : Category!.Id;
}
