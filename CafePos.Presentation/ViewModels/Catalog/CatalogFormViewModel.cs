using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Which catalogue entity the form sheet is editing.</summary>
public enum CatalogFormKind
{
    Product,
    Category,
    ModifierGroup,
    Ingredient,
    Combo
}

/// <summary>What the catalogue list asks the form sheet to open.</summary>
public sealed record CatalogFormRequest(CatalogFormKind Kind, Guid? EntityId = null);

/// <summary>
/// Hosts the five catalogue form ViewModels and routes a <see cref="CatalogFormRequest"/>
/// to the right one. The sheet page binds to the sub-ViewModels through the properties below.
/// </summary>
public class CatalogFormViewModel : ObservableObject
{
    private readonly ILogger<CatalogFormViewModel> logger;

    public CatalogFormViewModel(
        ProductFormViewModel product,
        CategoryFormViewModel category,
        ModifierFormViewModel modifier,
        IngredientFormViewModel ingredient,
        ComboFormViewModel combo,
        ILogger<CatalogFormViewModel> logger)
    {
        Product = product;
        Category = category;
        Modifier = modifier;
        Ingredient = ingredient;
        Combo = combo;
        this.logger = logger;

        // A save or a cancel in any sub-form closes the sheet.
        // The argument tells the caller whether the list should reload.
        Product.Saved += () => CloseRequested?.Invoke(true);
        Category.Saved += () => CloseRequested?.Invoke(true);
        Modifier.Saved += () => CloseRequested?.Invoke(true);
        Ingredient.Saved += () => CloseRequested?.Invoke(true);
        Combo.Saved += () => CloseRequested?.Invoke(true);
        Product.CancelRequested += () => CloseRequested?.Invoke(false);
        Category.CancelRequested += () => CloseRequested?.Invoke(false);
        Modifier.CancelRequested += () => CloseRequested?.Invoke(false);
        Ingredient.CancelRequested += () => CloseRequested?.Invoke(false);
        Combo.CancelRequested += () => CloseRequested?.Invoke(false);

        CancelCommand = new RelayCommand(Cancel);

        // A change on any sub-form marks the sheet dirty. Reset() runs on every load, so the flag is
        // cleared again the moment the next form opens.
        Product.PropertyChanged += (_, _) => IsDirty = true;
        Category.PropertyChanged += (_, _) => IsDirty = true;
        Modifier.PropertyChanged += (_, _) => IsDirty = true;
        Ingredient.PropertyChanged += (_, _) => IsDirty = true;
        Combo.PropertyChanged += (_, _) => IsDirty = true;
    }

    private void Cancel() => CloseRequested?.Invoke(false);

    /// <summary>Raised when the sheet should close. True = something was saved.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>
    /// Cancels whichever form is open. The page binds one Cancel in the toolbar and one in the
    /// shared footer, so the routing lives here rather than being repeated per form in XAML.
    /// </summary>
    public IRelayCommand CancelCommand { get; }

    /// <summary>
    /// True when the open form was changed since it was loaded. The page uses it to confirm before
    /// discarding — the hardware back gesture used to close the sheet silently, throwing away a
    /// half-filled product.
    /// </summary>
    public bool IsDirty { get; private set; }

    public ProductFormViewModel Product { get; }
    public CategoryFormViewModel Category { get; }
    public ModifierFormViewModel Modifier { get; }
    public IngredientFormViewModel Ingredient { get; }
    public ComboFormViewModel Combo { get; }

    private CatalogFormKind kind;
    public CatalogFormKind Kind
    {
        get => kind;
        private set
        {
            if (SetProperty(ref kind, value))
            {
                OnPropertyChanged(nameof(IsProductForm));
                OnPropertyChanged(nameof(IsCategoryForm));
                OnPropertyChanged(nameof(IsModifierForm));
                OnPropertyChanged(nameof(IsIngredientForm));
                OnPropertyChanged(nameof(IsComboForm));
                // Title is deliberately NOT raised here. It reads the active sub-form's Title,
                // and the sub-form still holds the previous entity's values until its LoadAsync
                // runs — raising it from the Kind setter flashed the previous form's title for a
                // frame. OpenAsync raises it once, after the load (see the end of that method).
            }
        }
    }

    public bool IsProductForm => Kind == CatalogFormKind.Product;
    public bool IsCategoryForm => Kind == CatalogFormKind.Category;
    public bool IsModifierForm => Kind == CatalogFormKind.ModifierGroup;
    public bool IsIngredientForm => Kind == CatalogFormKind.Ingredient;
    public bool IsComboForm => Kind == CatalogFormKind.Combo;

    public string Title => Kind switch
    {
        CatalogFormKind.Product => Product.Title,
        CatalogFormKind.Category => Category.Title,
        CatalogFormKind.ModifierGroup => Modifier.Title,
        CatalogFormKind.Combo => Combo.Title,
        _ => Ingredient.Title
    };

    /// <summary>Opens the sheet for the requested entity (null id = create new).</summary>
    public async Task OpenAsync(CatalogFormRequest request)
    {
        Kind = request.Kind;

        switch (request.Kind)
        {
            case CatalogFormKind.Product:
                await Product.LoadAsync(request.EntityId);
                break;
            case CatalogFormKind.Category:
                if (request.EntityId is { } categoryId) await Category.LoadAsync(categoryId);
                else Category.Reset();
                break;
            case CatalogFormKind.ModifierGroup:
                if (request.EntityId is { } groupId) await Modifier.LoadAsync(groupId);
                else Modifier.Reset();
                break;
            case CatalogFormKind.Ingredient:
                if (request.EntityId is { } ingredientId) await Ingredient.LoadAsync(ingredientId);
                else Ingredient.Reset();
                break;
            // Unlike the others, the bundle form takes a NULLABLE id and does its own branching:
            // it has to read the rest of the catalogue for a new bundle (to assign the sort order
            // that puts it at the end of the list) and only then decide there is nothing else to do.
            case CatalogFormKind.Combo:
                await Combo.LoadAsync(request.EntityId);
                break;
        }

        // Only now does the sub-form know whether it is editing or creating, so this is the
        // first point at which Title holds the right value.
        OnPropertyChanged(nameof(Title));

        // Every sub-form raises PropertyChanged as it loads (that is how the sheet re-renders), and
        // the dirty flag listens to those, so it is cleared once the load is done. From here on only
        // operator input can set it.
        IsDirty = false;
    }
}
