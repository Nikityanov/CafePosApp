using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>
/// Product card form (name, price, variants, photo, availability window, recipe).
/// Previously this state lived inside the 35 KB catalogue ViewModel and the form was built
/// imperatively in the page code-behind.
/// </summary>
public partial class ProductFormViewModel : ObservableObject, IFormFooterSource
{
    private readonly ICatalogService catalog;
    private readonly IFileService files;
    private readonly ILogger<ProductFormViewModel> logger;

    private Guid? editingProductId;
    private decimal costPrice;

    public ProductFormViewModel(ICatalogService catalog, IFileService files, ILogger<ProductFormViewModel> logger)
    {
        this.catalog = catalog;
        this.files = files;
        this.logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
        AddVariantCommand = new RelayCommand(AddVariant);
        RemoveVariantCommand = new RelayCommand<ProductVariant>(RemoveVariant);
        AddRecipeItemCommand = new AsyncRelayCommand(AddRecipeItemAsync);
        DeleteRecipeItemCommand = new AsyncRelayCommand<RecipeItem>(DeleteRecipeItemAsync);
        PickPhotoCommand = new AsyncRelayCommand(PickPhotoAsync);
    }

    /// <summary>Picker rows for the category: "без раздела" first, then the existing categories.</summary>
    public ObservableCollection<CatalogOption<Category>> CategoryOptions { get; } = [];

    /// <summary>Picker rows for the modifier group: "без группы" first, then the existing groups.</summary>
    public ObservableCollection<CatalogOption<ModifierGroup>> ModifierGroupOptions { get; } = [];

    public ObservableCollection<Ingredient> Ingredients { get; } = [];
    public ObservableCollection<ProductVariant> Variants { get; } = [];
    public ObservableCollection<RecipeItem> Recipes { get; } = [];
    public ObservableCollection<PriceHistoryEntry> PriceHistory { get; } = [];

    /// <summary>
    /// Empty-state flags. The variant / recipe / price-history lists are BindableLayouts rather than
    /// CollectionViews (a second scroller inside the form's ScrollView fought the outer one over the
    /// drag gesture, and the MaximumHeightRequest caps that worked around it were not a real sizing
    /// contract), and BindableLayout has no EmptyView — so the "nothing here yet" line is an ordinary
    /// label bound to these.
    /// </summary>
    public bool HasVariantsList => Variants.Count == 0;
    public bool HasRecipes => Recipes.Count == 0;
    public bool HasPriceHistory => PriceHistory.Count == 0;

    /// <summary>Re-announced whenever one of the three lists changes.</summary>
    public void NotifyEmptyStates()
    {
        // OnPropertyChanged takes a single name; there is no multi-name overload.
        OnPropertyChanged(nameof(HasVariantsList));
        OnPropertyChanged(nameof(HasRecipes));
        OnPropertyChanged(nameof(HasPriceHistory));
    }

    /// <summary>"Не задано" + 00:00..23:00 — used by the availability pickers.</summary>
    public IReadOnlyList<string> Hours { get; } =
        [.. Enumerable.Range(0, 25).Select(hour => hour == 0 ? "Не задано" : $"{hour - 1:00}:00")];

    public event Action? Saved;
    public event Action? CancelRequested;

    public bool IsEditing => editingProductId.HasValue;
    public string Title => IsEditing ? "Редактирование товара" : "Новый товар";
    public string SaveText => IsEditing ? "Сохранить изменения" : "Добавить товар";

    private string productName = string.Empty;
    public string ProductName { get => productName; set => SetProperty(ref productName, value); }

    private string productPriceText = string.Empty;
    public string ProductPriceText
    {
        get => productPriceText;
        set { if (SetProperty(ref productPriceText, value)) NotifyMargins(); }
    }

    private CatalogOption<Category>? selectedCategoryOption;
    public CatalogOption<Category>? SelectedCategoryOption
    {
        get => selectedCategoryOption;
        set { if (SetProperty(ref selectedCategoryOption, value)) OnPropertyChanged(nameof(SelectedCategory)); }
    }

    private CatalogOption<ModifierGroup>? selectedModifierGroupOption;
    public CatalogOption<ModifierGroup>? SelectedModifierGroupOption
    {
        get => selectedModifierGroupOption;
        set { if (SetProperty(ref selectedModifierGroupOption, value)) OnPropertyChanged(nameof(SelectedModifierGroup)); }
    }

    public Category? SelectedCategory => SelectedCategoryOption?.Value;

    public ModifierGroup? SelectedModifierGroup => SelectedModifierGroupOption?.Value;

    private bool hasVariants;
    public bool HasVariants
    {
        get => hasVariants;
        set
        {
            if (!SetProperty(ref hasVariants, value)) return;

            OnPropertyChanged(nameof(IsPriceEnabled));
            // A product with variants is priced per variant, so a base price is meaningless.
            if (value) ProductPriceText = string.Empty;
            NotifyMargins();
        }
    }

    /// <summary>The base price is locked while the product is priced by its variants.</summary>
    public bool IsPriceEnabled => !HasVariants;

    private string newVariantName = string.Empty;
    public string NewVariantName { get => newVariantName; set => SetProperty(ref newVariantName, value); }

    private string newVariantPriceText = string.Empty;
    public string NewVariantPriceText { get => newVariantPriceText; set => SetProperty(ref newVariantPriceText, value); }

    private string photoPath = string.Empty;
    public string PhotoPath
    {
        get => photoPath;
        set { if (SetProperty(ref photoPath, value)) OnPropertyChanged(nameof(HasPhoto)); }
    }

    public bool HasPhoto => !string.IsNullOrWhiteSpace(PhotoPath);

    private string allergens = string.Empty;
    public string Allergens { get => allergens; set => SetProperty(ref allergens, value); }

    private string tags = string.Empty;
    public string Tags { get => tags; set => SetProperty(ref tags, value); }

    private int fromHourIndex;
    public int FromHourIndex { get => fromHourIndex; set => SetProperty(ref fromHourIndex, value); }

    private int toHourIndex;
    public int ToHourIndex { get => toHourIndex; set => SetProperty(ref toHourIndex, value); }

    private Ingredient? selectedRecipeIngredient;
    public Ingredient? SelectedRecipeIngredient { get => selectedRecipeIngredient; set => SetProperty(ref selectedRecipeIngredient, value); }

    private string recipeQuantityText = string.Empty;
    public string RecipeQuantityText { get => recipeQuantityText; set => SetProperty(ref recipeQuantityText, value); }

    public decimal CostPrice => costPrice;

    /// <summary>
    /// Cheapest and priciest price the product can be sold for: the single base price, or the
    /// range covered by the variants (each variant carries its own price).
    /// </summary>
    private (decimal Low, decimal High) PriceRange
    {
        get
        {
            if (!HasVariants)
            {
                return TextFormat.TryParseDecimal(ProductPriceText, out var price) && price >= 0 ? (price, price) : (0m, 0m);
            }

            return Variants.Count > 0
                ? (Variants.Min(variant => variant.Price), Variants.Max(variant => variant.Price))
                : (0m, 0m);
        }
    }

    public decimal Margin => IsEditing && costPrice > 0 ? Money.Round(PriceRange.Low - costPrice) : 0;

    public decimal MarginPercent => IsEditing && costPrice > 0
        ? decimal.Round((PriceRange.Low - costPrice) / costPrice * 100, 1)
        : 0;

    public string CostPriceText => CostPrice > 0
        ? $"Себестоимость: {TextFormat.Money(CostPrice)}"
        : "Себестоимость не рассчитана — нет рецепта";

    public string MarginText
    {
        get
        {
            if (!IsEditing || costPrice <= 0) return string.Empty;

            var (low, high) = PriceRange;
            if (low <= 0 && high <= 0) return "Маржа: нет цен";

            var money = low == high
                ? TextFormat.Money(low)
                : $"{TextFormat.Money(low)} – {TextFormat.Money(high)}";
            return $"Маржа: {money} ({MarginPercent:0.#}%)";
        }
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set { if (SetProperty(ref validationMessage, value)) OnPropertyChanged(nameof(HasValidationMessage)); }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand AddVariantCommand { get; }
    public IRelayCommand<ProductVariant> RemoveVariantCommand { get; }
    public IAsyncRelayCommand AddRecipeItemCommand { get; }
    public IAsyncRelayCommand<RecipeItem> DeleteRecipeItemCommand { get; }
    public IAsyncRelayCommand PickPhotoCommand { get; }

    private void NotifyMargins()
    {
        OnPropertyChanged(nameof(Margin));
        OnPropertyChanged(nameof(MarginPercent));
        OnPropertyChanged(nameof(MarginText));
    }

    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(CostPrice));
        OnPropertyChanged(nameof(CostPriceText));
        NotifyMargins();
    }

}
