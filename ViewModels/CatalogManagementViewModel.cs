using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace CafePosApp.ViewModels;

/// <summary>
/// Catalogue management list: sections, category filtering, availability toggles and the row
/// overflow menus.
/// Create/edit forms live in ViewModels/Catalog/* and open through <see cref="ShowFormRequested"/>;
/// the page shows <see cref="Views.CatalogFormsPage"/> modally and reloads on save.
/// </summary>
/// <remarks>
/// What used to be here and is not, by the owner's decision: the product SearchBar, the
/// «Выбрать» selection mode and the bulk percentage-price adjustment it fed, and the CSV
/// export/import. The last of those moved to <see cref="SettingsViewModel"/> — see
/// <c>SettingsViewModel.Catalog.cs</c> for why, and for the confirmation the import gained on the
/// way. Nothing else moved with them, and this ViewModel no longer takes an <c>IFileService</c>.
/// </remarks>
public partial class CatalogManagementViewModel : ObservableObject
{
    private readonly ICatalogService catalog;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly ICatalogActionSheet actionSheet;
    private readonly ILogger<CatalogManagementViewModel> logger;

    private readonly List<Product> allProducts = [];

    /// <summary>
    /// How long a filter change waits before re-filtering.
    /// </summary>
    /// <remarks>
    /// This timer used to serve two things — the product SearchBar and the category chips — and the
    /// SearchBar is gone from this page by the owner's decision. The timer therefore has one caller
    /// left, <see cref="SelectedProductCategory"/>, and it is still worth having there, which is
    /// what the previous note got wrong when it described the debounce as having gone with the
    /// search box. The chip is a single discrete tap with nothing to debounce on its own account;
    /// what it needs the delay for is <see cref="RebuildCategoryFilters"/>, which reassigns
    /// <see cref="SelectedProductCategory"/> on every load. Without the delay each load scheduled a
    /// filter pass that raced the load that scheduled it.
    /// <para>
    /// <see cref="ProductSearchText"/> is the second caller and it stays too. Nothing binds it any
    /// more — that is the whole point of the search's removal — but the in-memory filter and its
    /// debounce are the property's behaviour, not the control's, so they were left intact rather
    /// than made conditional on a SearchBar that is not coming back.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? filterCancellation;

    /// <summary>
    /// True while a load is running. Drives the RefreshView spinner through a TwoWay binding,
    /// so it must be set back to false on every exit path — see <see cref="LoadAsync"/>.
    /// </summary>
    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    public CatalogManagementViewModel(
        ICatalogService catalog,
        IDialogService dialogs,
        IHapticService haptics,
        ICatalogActionSheet actionSheet,
        ILogger<CatalogManagementViewModel> logger)
    {
        this.catalog = catalog;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.actionSheet = actionSheet;
        this.logger = logger;

        // No AllowConcurrentExecutions: the option made the command runnable while a load was
        // already in flight, which contradicted LoadAsync's own guard and let the RefreshView
        // spinner never resolve. Without the option the command is disabled while it runs.
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SetSectionCommand = new RelayCommand<string>(SetSection);
        ShowAddFormCommand = new RelayCommand(ShowAddForm);
        SelectCategoryFilterCommand = new RelayCommand<CategoryFilterChip>(SelectCategoryFilter);
        ToggleShowDeletedCommand = new RelayCommand(() => ShowDeleted = !ShowDeleted);
        EditProductCommand = new RelayCommand<Product>(product => RequestForm(CatalogFormKind.Product, product?.Id));
        CopyProductCommand = new AsyncRelayCommand<Product>(CopyProductAsync);
        DeleteProductCommand = new AsyncRelayCommand<Product>(DeleteProductAsync);
        RestoreProductCommand = new AsyncRelayCommand<Product>(RestoreProductAsync);
        ToggleProductAvailabilityCommand = new AsyncRelayCommand<Product>(ToggleProductAvailabilityAsync);
        EditCategoryCommand = new RelayCommand<Category>(category => RequestForm(CatalogFormKind.Category, category?.Id));
        DeleteCategoryCommand = new AsyncRelayCommand<Category>(DeleteCategoryAsync);
        EditModifierGroupCommand = new RelayCommand<ModifierOptionGroup>(group => RequestForm(CatalogFormKind.ModifierGroup, group?.GroupId));
        DeleteModifierGroupCommand = new AsyncRelayCommand<ModifierOptionGroup>(DeleteModifierGroupAsync);
        ToggleModifierOptionAvailabilityCommand = new AsyncRelayCommand<ModifierOption>(ToggleModifierOptionAvailabilityAsync);
        RemoveModifierOptionCommand = new AsyncRelayCommand<ModifierOption>(RemoveModifierOptionAsync);
        EditIngredientCommand = new RelayCommand<Ingredient>(ingredient => RequestForm(CatalogFormKind.Ingredient, ingredient?.Id));
        DeleteIngredientCommand = new AsyncRelayCommand<Ingredient>(DeleteIngredientAsync);
        ToggleIngredientAvailabilityCommand = new AsyncRelayCommand<Ingredient>(ToggleIngredientAvailabilityAsync);
        ProductActionsCommand = new AsyncRelayCommand<Product>(ShowProductActionsAsync);
        CategoryActionsCommand = new AsyncRelayCommand<Category>(ShowCategoryActionsAsync);
        ModifierGroupActionsCommand = new AsyncRelayCommand<ModifierOptionGroup>(ShowModifierGroupActionsAsync);
        ModifierOptionActionsCommand = new AsyncRelayCommand<ModifierOption>(ShowModifierOptionActionsAsync);
        IngredientActionsCommand = new AsyncRelayCommand<Ingredient>(ShowIngredientActionsAsync);

        // The strip is built from a fixed list, so the default section is marked here rather than
        // by re-entering SetSection (which would early-return on the no-op).
        Sections[0].IsSelected = true;
    }

    /// <summary>Raised when a form sheet should open (FAB add or edit button).</summary>
    public event Action<CatalogFormRequest>? ShowFormRequested;

    public ObservableCollection<Product> FilteredProducts { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<ModifierOptionGroup> ModifierOptionGroups { get; } = new();
    public ObservableCollection<Ingredient> Ingredients { get; } = new();

    /// <summary>Category-filter chips: "Все" followed by one chip per category.</summary>
    public ObservableCollection<CategoryFilterChip> CategoryFilters { get; } = new();

    // ─── Responsive width contract ──────────────────────────────────────────────────────────────────
    // Same pattern as MenuViewModel.ProductColumnSpan: the window width is pushed in from the page
    // (a Shell-hosted ContentPage never gets OnSizeAllocated), and the product grid's column span
    // is derived from it. ItemsLayout is a BindableObject outside the visual tree, so {Binding} on
    // Span does not reliably inherit the page's BindingContext — the span is assigned from code.
    // The constants mirror Views/CatalogManagementPage.xaml and must be changed with it.

    private const double PageHorizontalPadding = 24;      // content Grid Padding="12" both sides
    private const double ProductColumnSpacing = 4;        // GridItemsLayout HorizontalItemSpacing
    private const double MaxContentWidth = 1400;          // rows stop stretching past this
    private const double MinimumProductCardWidth = 280;   // below this a card is cramped

    private double availableWidth;

    /// <summary>
    /// The window's width in device independent pixels, pushed in from <c>Views.CatalogManagementPage</c>
    /// whenever it is resized. Feeds <see cref="ProductColumnSpan"/>; not bound from XAML.
    /// </summary>
    public double AvailableWidth
    {
        get => availableWidth;
        set
        {
            // Resizing produces a stream of fractional widths; a one-pixel threshold keeps the
            // property — and therefore the whole grid — from being re-laid out on every frame.
            if (Math.Abs(availableWidth - value) < 1) return;
            availableWidth = value;
            OnPropertyChanged(nameof(AvailableWidth));
            OnPropertyChanged(nameof(ProductColumnSpan));
        }
    }

    /// <summary>
    /// Product cards per row: derived from a target minimum card width, not raw breakpoints.
    /// The column count is floor(contentWidth / (minCardWidth + spacing)), clamped to 1–3.
    /// Applied by the page to the named <c>GridItemsLayout</c>, which is the only width-related
    /// member the grid still has (ItemWidth was removed from ItemsLayout in MAUI 10).
    /// </summary>
    public int ProductColumnSpan
    {
        get
        {
            if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
                return 1;

            var content = Math.Min(availableWidth, MaxContentWidth) - PageHorizontalPadding;
            var columns = (int)(content / (MinimumProductCardWidth + ProductColumnSpacing));
            return Math.Clamp(columns, 1, 3);
        }
    }

    // ─── Sections ───

    /// <summary>Keys of the four catalogue entities, in header order.</summary>
    public const string ProductsSection = "products";
    public const string CategoriesSection = "categories";
    public const string ModifiersSection = "modifiers";
    public const string IngredientsSection = "ingredients";

    /// <summary>The entity switcher strip. Built once — the four entries are fixed.</summary>
    public ObservableCollection<CatalogSection> Sections { get; } =
    [
        new(ProductsSection, "Товары"),
        new(CategoriesSection, "Разделы"),
        new(ModifiersSection, "Модификаторы"),
        new(IngredientsSection, "Ингредиенты")
    ];

    private string section = ProductsSection;
    public string Section => section;
    public bool IsProductsSectionVisible => section == ProductsSection;
    public bool IsCategoriesSectionVisible => section == CategoriesSection;
    public bool IsModifiersSectionVisible => section == ModifiersSection;
    public bool IsIngredientsSectionVisible => section == IngredientsSection;

    /// <summary>
    /// Label on the add button, which doubles as its screen-reader name.
    /// </summary>
    /// <remarks>
    /// The button adds whatever entity is open, so a static "Добавить" would be wrong on three
    /// of the four sections. Material 3 asks for one or two words on an extended FAB —
    /// "Добавить группу модификаторов" was four and pushed the pill across a third of a phone
    /// screen, so the modifiers section names the thing an operator thinks of instead of the
    /// record type.
    /// </remarks>
    public string AddButtonText => section switch
    {
        CategoriesSection => "Добавить раздел",
        ModifiersSection => "Добавить модификатор",
        IngredientsSection => "Добавить ингредиент",
        _ => "Добавить товар"
    };

    private void SetSection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == section) return;
        section = value;
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(IsProductsSectionVisible));
        OnPropertyChanged(nameof(IsCategoriesSectionVisible));
        OnPropertyChanged(nameof(IsModifiersSectionVisible));
        OnPropertyChanged(nameof(IsIngredientsSectionVisible));
        OnPropertyChanged(nameof(AddButtonText));
        foreach (var entry in Sections)
            entry.IsSelected = entry.Key == value;
    }


    // ─── Filters ───

    private string productSearchText = string.Empty;
    public string ProductSearchText
    {
        get => productSearchText;
        set { if (SetProperty(ref productSearchText, value)) ScheduleFilterRefresh(); }
    }

    private Category? selectedProductCategory;
    public Category? SelectedProductCategory
    {
        get => selectedProductCategory;
        set
        {
            if (SetProperty(ref selectedProductCategory, value))
            {
                // Debounced for the reason recorded on SearchDebounce: RebuildCategoryFilters
                // reassigns this on every load, so an immediate refresh would run a filter pass
                // against a half-populated Categories collection.
                ScheduleFilterRefresh();
                SyncCategoryFilterChips();
            }
        }
    }

    /// <summary>When on, soft-deleted products are listed too (with a restore button).</summary>
    private bool showDeleted;
    public bool ShowDeleted
    {
        get => showDeleted;
        set
        {
            if (!SetProperty(ref showDeleted, value)) return;
            // Fire-and-forget is unavoidable in a property setter, but the reload must actually
            // happen: LoadAsync no longer bails on IsBusy, so the new value is picked up even if
            // a previous load is still running (the newer read wins — see LoadAsync).
            _ = LoadAsync();
        }
    }

    // ─── Counts / state ───

    public int LowStockCount => Ingredients.Count(ingredient => ingredient.IsLowStock);
    public bool HasLowStock => LowStockCount > 0;

    // The counters were rendered with a bare "{0} товаров" format string, which produced
    // "1 товаров" / "2 разделов". Composed here so the plural rule lives in one place.

    public string ProductsCountText => $"{FilteredProducts.Count} {Plural(FilteredProducts.Count, "товар", "товара", "товаров")}";

    public string CategoriesCountText => $"{Categories.Count} {Plural(Categories.Count, "раздел", "раздела", "разделов")}";

    public string ModifierGroupsCountText => $"{ModifierOptionGroups.Count} {Plural(ModifierOptionGroups.Count, "группа", "группы", "групп")}";

    public string IngredientsCountText => $"{Ingredients.Count} {Plural(Ingredients.Count, "ингредиент", "ингредиента", "ингредиентов")}";

    public string LowStockText => $"! {LowStockCount} {Plural(LowStockCount, "ингредиент заканчивается", "ингредиента заканчивается", "ингредиентов заканчивается")}";

    /// <summary>Russian plural selection: one / few / many.</summary>
    private static string Plural(int count, string one, string few, string many)
    {
        if ((uint)(count % 100) is >= 11 and <= 14) return many;
        return (uint)(count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    /// <summary>
    /// The severity of <see cref="ValidationMessage"/>. The one notice label carries both
    /// successes ("Скопировано: …") and errors, so the colour and the leading icon are driven
    /// by this flag — colour alone would not tell them apart.
    /// </summary>
    // Named NoticeLevel, not NoticeSeverity: a nested enum may not share its name with a
    // property of the declaring class (CS0102). The *property* keeps the name NoticeSeverity
    // because that is what the page binds.
    public enum NoticeLevel { None, Success, Error }

    private string validationMessage = string.Empty;
    private NoticeLevel noticeSeverity;
    public string ValidationMessage => validationMessage;
    public NoticeLevel NoticeSeverity => noticeSeverity;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(validationMessage);

    /// <summary>
    /// Sets the notice text and its severity together so the two can never disagree.
    /// </summary>
    private void SetNotice(string message, NoticeLevel severity)
    {
        validationMessage = message;
        noticeSeverity = severity;
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(NoticeSeverity));
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    // ─── Commands ───

    public IAsyncRelayCommand LoadCommand { get; }
    public IRelayCommand<string> SetSectionCommand { get; }
    public IRelayCommand ShowAddFormCommand { get; }
    public IRelayCommand<CategoryFilterChip> SelectCategoryFilterCommand { get; }
    public IRelayCommand ToggleShowDeletedCommand { get; }
    public IRelayCommand<Product> EditProductCommand { get; }
    public IAsyncRelayCommand<Product> CopyProductCommand { get; }
    public IAsyncRelayCommand<Product> DeleteProductCommand { get; }
    public IAsyncRelayCommand<Product> RestoreProductCommand { get; }
    public IAsyncRelayCommand<Product> ToggleProductAvailabilityCommand { get; }
    public IRelayCommand<Category> EditCategoryCommand { get; }
    public IAsyncRelayCommand<Category> DeleteCategoryCommand { get; }
    public IRelayCommand<ModifierOptionGroup> EditModifierGroupCommand { get; }
    public IAsyncRelayCommand<ModifierOptionGroup> DeleteModifierGroupCommand { get; }
    public IAsyncRelayCommand<ModifierOption> ToggleModifierOptionAvailabilityCommand { get; }
    public IAsyncRelayCommand<ModifierOption> RemoveModifierOptionCommand { get; }
    public IRelayCommand<Ingredient> EditIngredientCommand { get; }
    public IAsyncRelayCommand<Ingredient> DeleteIngredientCommand { get; }
    public IAsyncRelayCommand<Ingredient> ToggleIngredientAvailabilityCommand { get; }

    // Row overflow menus. MAUI has no context menu, so each row opens an action sheet and the
    // chosen key is dispatched to the command that was already there.
    public IAsyncRelayCommand<Product> ProductActionsCommand { get; }
    public IAsyncRelayCommand<Category> CategoryActionsCommand { get; }
    public IAsyncRelayCommand<ModifierOptionGroup> ModifierGroupActionsCommand { get; }
    public IAsyncRelayCommand<ModifierOption> ModifierOptionActionsCommand { get; }
    public IAsyncRelayCommand<Ingredient> IngredientActionsCommand { get; }

    // ─── Load ───

    /// <summary>
    /// Loads the whole catalogue. Re-entrancy: the command is constructed without
    /// <see cref="AsyncRelayCommandOptions.AllowConcurrentExecutions"/> so it cannot start a
    /// second run, and the method no longer bails on <see cref="IsBusy"/>. The old bare
    /// <c>if (IsBusy) return;</c> was the pull-to-refresh trap: RefreshView sets IsRefreshing
    /// true, the TwoWay binding pushed that into IsBusy, and the guard then returned without
    /// ever clearing the flag — the spinner spun forever (dotnet/maui#12469).
    /// <para>
    /// Callers that may overlap (the ShowDeleted setter, which cannot await) start a fresh run
    /// that re-reads ShowDeleted; the later-started run finishes last, so its value is the one
    /// on screen.
    /// </para>
    /// </summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var products = await catalog.GetProductsAsync(ShowDeleted);
            allProducts.Clear();
            allProducts.AddRange(products);

            Categories.SyncWith(await catalog.GetCategoriesAsync(), category => category.Id);
            RebuildCategoryFilters();

            var groups = await catalog.GetModifierGroupsAsync();
            ModifierOptionGroups.SyncWith(
                groups.Select(group => new ModifierOptionGroup(group.Id, group.Name, group.Options)),
                group => group.GroupId);

            Ingredients.SyncWith(await catalog.GetIngredientsAsync(), ingredient => ingredient.Id);

            RefreshFilteredProducts();
            NotifyCounts();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load the catalogue");
            SetNotice(UserMessages.Describe(exception, "Не удалось загрузить каталог"), NoticeLevel.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds the filter chip strip. Chip instances are recreated on every load (the same
    /// pattern the other lists use), so the selected state is re-applied from
    /// <see cref="SelectedProductCategory"/> rather than carried across.
    /// </summary>
    private void RebuildCategoryFilters()
    {
        var previousId = SelectedProductCategory?.Id;
        CategoryFilters.Clear();
        CategoryFilters.Add(new CategoryFilterChip(null, "Все"));
        foreach (var category in Categories)
            CategoryFilters.Add(new CategoryFilterChip(category, category.Name));

        // Keep the operator's filter across a reload, or drop it if that category is gone.
        SelectedProductCategory = Categories.FirstOrDefault(category => category.Id == previousId);
        SyncCategoryFilterChips();
    }

    private void SyncCategoryFilterChips()
    {
        foreach (var chip in CategoryFilters)
            chip.IsSelected = chip.Category is null
                ? SelectedProductCategory is null
                : SelectedProductCategory is not null && chip.Category.Id == SelectedProductCategory.Id;
    }

    private void SelectCategoryFilter(CategoryFilterChip? chip)
    {
        if (chip is null) return;
        SelectedProductCategory = chip.Category;
    }

    private void RefreshFilteredProducts()
    {
        IEnumerable<Product> query = allProducts;
        if (!string.IsNullOrWhiteSpace(ProductSearchText))
            query = query.Where(product => product.Name.Contains(ProductSearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (SelectedProductCategory is not null)
            query = query.Where(product => product.CategoryId == SelectedProductCategory.Id);

        FilteredProducts.SyncWith(query, product => product.Id);

        OnPropertyChanged(nameof(ProductsCountText));
    }

    /// <summary>
    /// Restarts the debounce timer on every filter change, so the filter runs once the operator
    /// stops acting. It used to fire per keystroke, which re-scanned the whole catalogue and
    /// re-diffed the list on each character and stutters on a long catalogue.
    /// </summary>
    private void ScheduleFilterRefresh()
    {
        // Cancel the pending run. Not disposing the source is deliberate: Task.Delay registers a
        // timer rather than a wait handle, so there is no OS resource to release.
        Interlocked.Exchange(ref filterCancellation, new CancellationTokenSource())?.Cancel();

        var token = filterCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounce, token);
                if (token.IsCancellationRequested) return;

                // The timer thread must not touch the bound collections.
                MainThread.BeginInvokeOnMainThread(RefreshFilteredProducts);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer filter change — the newest one does the work.
            }
        }, token);
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(ProductsCountText));
        OnPropertyChanged(nameof(CategoriesCountText));
        OnPropertyChanged(nameof(ModifierGroupsCountText));
        OnPropertyChanged(nameof(IngredientsCountText));
        OnPropertyChanged(nameof(HasLowStock));
        OnPropertyChanged(nameof(LowStockText));
    }

    private void RequestForm(CatalogFormKind kind, Guid? entityId) =>
        ShowFormRequested?.Invoke(new CatalogFormRequest(kind, entityId));

    private void ShowAddForm() => RequestForm(section switch
    {
        "categories" => CatalogFormKind.Category,
        "modifiers" => CatalogFormKind.ModifierGroup,
        "ingredients" => CatalogFormKind.Ingredient,
        _ => CatalogFormKind.Product
    }, null);
}

