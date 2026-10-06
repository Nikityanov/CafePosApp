using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// What one slot of a bundle is charged inside it — the three states of
/// <see cref="ComboComponent.ComponentPriceKopecks"/>, named rather than left as a nullable number.
/// </summary>
/// <remarks>
/// The three states are genuinely three different decisions, not one number with three spellings:
/// <see cref="DishPrice"/> is <c>null</c> (the dish's own price — the normal case), <see cref="Free"/>
/// is <c>0</c> ("included, no charge") and <see cref="Fixed"/> is an amount. Collapsing them into
/// "price" and leaving 0 to mean either "free" or "mistyped zero" is how a bundle ends up quietly
/// giving something away, so the form asks the question by name and the model keeps the three apart.
/// </remarks>
public enum ComboPriceMode
{
    /// <summary>The dish's own price (<c>ComponentPriceKopecks == null</c>).</summary>
    DishPrice,

    /// <summary>Included in the bundle and charged nothing (<c>0</c>).</summary>
    Free,

    /// <summary>Charged this instead of the dish's price (Simphony's "Add Side Prices").</summary>
    Fixed
}

/// <summary>One picker row of <see cref="ComboPriceMode"/>, so the choice reads as three sentences.</summary>
public sealed record ComboPriceModeOption(ComboPriceMode Mode, string Name);

/// <summary>
/// Bundle create/edit form: a name, a set of slots, and the price the bundle sells for.
/// </summary>
/// <remarks>
/// The price is a field the manager types, and it is required: <see cref="IComboService.SaveComboAsync"/>
/// refuses a bundle at zero or less, because a sellable item at no price is a catalogue error rather
/// than a bargain. The à la carte sum of the slots is shown beside it as a read-only reference
/// («По отдельности 550,00 ₿») — it is what the discount is measured against, never a second price.
/// <para>
/// Slots are edited as <see cref="ComboSlotRow"/>s rather than as bare <see cref="ComboComponent"/>s
/// because each one carries two things the entity does not: a picker of products and the three-way
/// price choice, and a computed line total. Saving hands Core the whole slot set, which is a FULL
/// REPLACEMENT (see <see cref="IComboService.SaveComboAsync"/>) — so a slot removed here is deleted on
/// save, which is why removal is an explicit per-row action rather than a swipe or a hidden gesture.
/// </para>
/// </remarks>
public partial class ComboFormViewModel : ObservableObject, IFormFooterSource
{
    /// <summary>Text of the "no substitute" picker row.</summary>
    private const string NoSubstituteText = "— Без замены —";

    private readonly IComboService combos;
    private readonly ICatalogService catalog;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly ILogger<ComboFormViewModel> logger;

    private Guid? editingComboId;
    private int sortOrder;

    public ComboFormViewModel(
        IComboService combos,
        ICatalogService catalog,
        IDialogService dialogs,
        IHapticService haptics,
        ILogger<ComboFormViewModel> logger)
    {
        this.combos = combos;
        this.catalog = catalog;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.logger = logger;

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke());
        AddSlotCommand = new RelayCommand(AddSlot);
        RemoveSlotCommand = new AsyncRelayCommand<ComboSlotRow>(RemoveSlotAsync);

        // The stepper's two commands live on the FORM and take the row as a parameter, exactly as the
        // cart's stepper does — not on the row itself.
        //
        // That is not tidiness, it is a measured fact. Wired directly to the row's own RelayCommand, the
        // "+" rendered in exactly the right place and simply did not answer a tap: same coordinates, same
        // style, same row, and the "×" beside it — which resolves its command through the page — fired
        // every time. Routing both through the form is the arrangement that is demonstrably live on the
        // device, and it keeps one mechanism for "change this line's quantity" across the whole app
        // instead of two, one of which was silently dead.
        // The null-check is unreachable rather than defensive: the only CommandParameter bound to these is
        // the row itself, from inside the slot template, so a null there means the row is not on screen
        // and nothing pressed the button. It is spelled out instead of suppressed because a stepper
        // that silently does nothing when handed the wrong thing is the exact failure this form has
        // already produced once.
        StepComponentQuantityCommand = new RelayCommand<ComboSlotRow>(row => row?.StepQuantity(1));
        StepComponentQuantityDownCommand = new RelayCommand<ComboSlotRow>(row => row?.StepQuantity(-1));
    }

    public event Action? Saved;
    public event Action? CancelRequested;

    /// <summary>
    /// The three price choices, in the order they are offered. A plain list, not a collection: the
    /// options are fixed for the life of the form, so there is nothing to notify about.
    /// </summary>
    public IReadOnlyList<ComboPriceModeOption> PriceModes { get; } =
    [
        new(ComboPriceMode.DishPrice, "Цена блюда"),
        new(ComboPriceMode.Free, "Бесплатно (включено в комбо)"),
        new(ComboPriceMode.Fixed, "Своя цена компонента")
    ];

    /// <summary>Live dishes for the component picker and the substitute picker. No "none" row: a slot must name a dish.</summary>
    public ObservableCollection<CatalogOption<Product>> ProductOptions { get; } = [];

    /// <summary>The same dishes with an explicit "— Без замены —" row first, since a substitute is optional.</summary>
    public ObservableCollection<CatalogOption<Product>> SubstituteOptions { get; } = [];

    /// <summary>The slots as the form holds them. What is saved, in full.</summary>
    public ObservableCollection<ComboSlotRow> Components { get; } = [];

    public bool IsEditing => editingComboId.HasValue;
    public string Title => IsEditing ? "Редактирование комбо" : "Новое комбо";
    public string SaveText => IsEditing ? "Сохранить изменения" : "Добавить комбо";

    private string comboName = string.Empty;
    public string ComboName { get => comboName; set => SetProperty(ref comboName, value); }

    /// <summary>The dish picked for the row that is about to be added. Null = nothing chosen yet.</summary>
    private CatalogOption<Product>? newSlotProductOption;
    public CatalogOption<Product>? NewSlotProductOption
    {
        get => newSlotProductOption;
        set => SetProperty(ref newSlotProductOption, value);
    }

    private string newSlotQuantityText = "1";
    public string NewSlotQuantityText { get => newSlotQuantityText; set => SetProperty(ref newSlotQuantityText, value); }

    // ── The price: typed by the owner, measured against the sum ─────────────────────────────────

    /// <summary>
    /// The à la carte sum of the slots, in kopecks. A REFERENCE, not a price — it is what the
    /// discount is measured against, and it never reaches the customer. Recomputed on every slot
    /// change so the comparison under the price field is always live.
    /// </summary>
    private long referenceKopecks;
    public long ReferenceKopecks { get => referenceKopecks; private set => SetProperty(ref referenceKopecks, value); }

    /// <summary>The bundle's own price in kopecks, as the owner typed it. What the till charges.</summary>
    private long comboPriceKopecks;
    public long ComboPriceKopecks { get => comboPriceKopecks; private set => SetProperty(ref comboPriceKopecks, value); }

    /// <summary>
    /// The price as the owner types it. Two-way bound to the field; parsed on every keystroke so the
    /// discount under it is live. An unparseable entry holds the last valid figure for the arithmetic
    /// but is flagged invalid, so the save is refused rather than silently writing a stale price.
    /// </summary>
    private string comboPriceText = string.Empty;
    public string ComboPriceText
    {
        get => comboPriceText;
        set
        {
            if (!SetProperty(ref comboPriceText, value ?? string.Empty)) return;

            if (TextFormat.TryParseDecimal(comboPriceText.Trim(), out var amount) && amount > 0)
            {
                if (!isComboPriceValid)
                {
                    isComboPriceValid = true;
                    OnPropertyChanged(nameof(IsComboPriceValid));
                }

                var kopecks = Money.ToKopecks(amount);
                if (kopecks != comboPriceKopecks)
                {
                    comboPriceKopecks = kopecks;
                    OnPropertyChanged(nameof(ComboPriceKopecks));
                }
            }
            else if (isComboPriceValid)
            {
                isComboPriceValid = false;
                OnPropertyChanged(nameof(IsComboPriceValid));
            }

            OnPropertyChanged(nameof(DiscountText));
            OnPropertyChanged(nameof(DiscountHint));
        }
    }

    /// <summary>False while the price field holds something that is not a positive number.</summary>
    private bool isComboPriceValid;
    public bool IsComboPriceValid => isComboPriceValid;

    /// <summary>
    /// The à la carte sum, formatted — the read-only reference line under the price field. Empty
    /// while there are no slots, because there is nothing to compare against.
    /// </summary>
    public string ReferenceText => Components.Count == 0
        ? string.Empty
        : $"По отдельности {TextFormat.Money(Money.FromKopecks(ReferenceKopecks))}";

    /// <summary>
    /// What the typed price works out to against the à la carte sum, in words. The point of the
    /// field: the owner decides the price and sees immediately what it means as a saving or a
    /// surcharge. Empty when there is nothing to compare against — a bundle whose parts cost nothing
    /// has no percentage, and «0%» would be a false statement about a bundle the customer pays for.
    /// </summary>
    public string DiscountText
    {
        get
        {
            if (Components.Count == 0 || !isComboPriceValid) return string.Empty;

            var percent = ComboPricing.DiscountPercent(ReferenceKopecks, comboPriceKopecks);
            if (percent is null) return string.Empty;

            var difference = Money.FromKopecks(ReferenceKopecks - comboPriceKopecks);
            return ComboPricing.Compare(ReferenceKopecks, comboPriceKopecks) switch
            {
                ComboPriceRelation.Cheaper => $"Скидка {FormatPercent(percent.Value)} — выгода {TextFormat.Money(difference)}",
                ComboPriceRelation.Dearer => $"Наценка {FormatPercent(-percent.Value)} — разница {TextFormat.Money(-difference)}",
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Why the percentage is or is not shown. Stated rather than left blank: a manager who types a
    /// price and sees no discount needs to know whether that is because the two agree or because
    /// there is nothing to compare against — the two have opposite meanings for what to do next.
    /// </summary>
    /// <remarks>
    /// EMPTY for the ordinary case, and that is the owner's instruction: a bundle priced below its
    /// parts is what a bundle IS, and explaining it on every save burned a line to say nothing the
    /// operator did not already know. The percentage directly above already states the outcome as a
    /// figure. The three remaining cases are anomalies and each tells the operator something to do:
    /// a surcharge is legal but not obvious, an equal price means the bundle saves nothing, and
    /// components that cost nothing leave nothing to compare against.
    /// </remarks>
    public string DiscountHint
    {
        get
        {
            if (Components.Count == 0) return "Добавьте компоненты — тогда будет с чем сравнивать цену.";
            if (!isComboPriceValid) return "Введите цену комбо — пока она не задана, скидку не посчитать.";

            return ComboPricing.Compare(ReferenceKopecks, comboPriceKopecks) switch
            {
                ComboPriceRelation.Cheaper => string.Empty,
                ComboPriceRelation.Dearer => "Цена выше суммы компонентов — это наценка, она разрешена.",
                ComboPriceRelation.Equal => "Цена равна сумме компонентов — скидки нет.",
                _ => "Компоненты ничего не стоят — сравнивать не с чем."
            };
        }
    }

    /// <summary>
    /// Formats a percentage the way a calculator shows it: two decimals, trailing zeros dropped, so
    /// «19%» reads as 19 and «6,25%» as 6,25 rather than as a figure with padding nobody typed.
    /// </summary>
    private static string FormatPercent(decimal percent) =>
        percent.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + "%";

    public int SlotCount => Components.Count;

    public string SlotCountText => $"{Components.Count} {TextFormat.Plural(Components.Count, "компонент", "компонента", "компонентов")}";

    /// <summary>BindableLayout has no EmptyView, so «Компонентов пока нет.» is a label bound to this.</summary>
    public bool HasNoComponents => Components.Count == 0;

    /// <summary>
    /// Wording for what the save does to a removed slot. Stated on the form rather than discovered:
    /// <see cref="IComboService.SaveComboAsync"/> REPLACES the whole slot set, so leaving a slot out
    /// of the form deletes it from the catalogue — which is the point, and is also why an
    /// unmentioned change would be a silent data loss rather than a no-op.
    /// </summary>
    public string ReplacementNote =>
        "Сохранение заменяет состав целиком: убранный здесь компонент будет удалён из комбо.";

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
    public IRelayCommand AddSlotCommand { get; }
    public IAsyncRelayCommand<ComboSlotRow> RemoveSlotCommand { get; }

    /// <summary>Adds one unit of the dish in the row the stepper was pressed on.</summary>
    public IRelayCommand<ComboSlotRow> StepComponentQuantityCommand { get; }

    /// <summary>
    /// Takes one unit off the row the stepper was pressed on. No CanExecute: it is refused in
    /// <see cref="ComboSlotRow.StepQuantity"/> instead, because a bundle takes whole units of a dish
    /// and the refusal has to be the same rule for every caller — a button that greys out is a
    /// different decision from a press that changes nothing.
    /// </summary>
    public IRelayCommand<ComboSlotRow> StepComponentQuantityDownCommand { get; }

    /// <summary>
    /// Re-counts the à la carte reference from the slots. Called by every slot row whenever anything
    /// about it changes, which is what makes the comparison under the price field live: no refresh
    /// button, no second copy to press.
    /// </summary>
    private void RecalculateTotal()
    {
        ReferenceKopecks = ComboPricing.ReferenceKopecks(
            Components.Select(slot => new ComboComponentPrice(slot.QuantityPerUnit, slot.UnitKopecks)));
        OnPropertyChanged(nameof(ReferenceText));
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(DiscountHint));
        NotifySlots();
    }

    /// <summary>
    /// Re-announces what only depends on the slot COUNT. Adding to and removing from
    /// <see cref="Components"/> do not know these properties exist, so without this the empty-state
    /// line and the slot counter would go stale the moment the first slot is added.
    /// </summary>
    private void NotifySlots()
    {
        OnPropertyChanged(nameof(SlotCount));
        OnPropertyChanged(nameof(SlotCountText));
        OnPropertyChanged(nameof(HasNoComponents));
        OnPropertyChanged(nameof(ReferenceText));
        OnPropertyChanged(nameof(DiscountText));
        OnPropertyChanged(nameof(DiscountHint));
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SaveText));
    }

    /// <summary>
    /// The slot rows as <see cref="Components"/> holds them, converted for the save: ids kept so a
    /// slot that survives an edit keeps its identity, and only the four fields the entity has.
    /// </summary>
    private List<ComboComponent> BuildSlots(Guid comboId) =>
    [
        .. Components.Select(slot => new ComboComponent
        {
            Id = slot.Id,
            ComboId = comboId,
            ProductId = slot.Product!.Id,
            QuantityPerUnit = slot.QuantityPerUnit,
            ComponentPriceKopecks = slot.ComponentPriceKopecks,
            SubstituteProductId = slot.SubstituteProductId
        })
    ];
}

/// <summary>
/// One editable slot of a bundle: the dish, how many of it, what it is charged, and what replaces it
/// when it runs out.
/// </summary>
/// <remarks>
/// A ViewModel rather than the <see cref="ComboComponent"/> itself, and the reason is the price mode:
/// the entity stores <c>long?</c> and three distinct values, which no control can present honestly.
/// Two pickers also have to hold state per row, and a shared <c>Picker.ItemsSource</c> cannot also be
/// the row's own selection.
/// <para>
/// Every field re-reads <see cref="UnitKopecks"/> through <see cref="ComboPricing.ResolveUnitKopecks"/>
/// rather than storing a resolved number, which is the same rule the sale side applies — so what the
/// manager sees here is exactly what the till will charge, including across a substitution.
/// </para>
/// </remarks>
public sealed class ComboSlotRow : ObservableObject
{
    private readonly Action changed;

    private CatalogOption<Product>? selectedProductOption;
    private CatalogOption<Product>? selectedSubstituteOption;
    private ComboPriceModeOption? selectedPriceModeOption;
    private ComboPriceMode priceMode = ComboPriceMode.DishPrice;
    private string quantityText = "1";
    private string fixedPriceText = string.Empty;

    /// <summary>Last valid multiplicity. Held while the field is mid-typing so the live total keeps meaning something.</summary>
    private int quantityPerUnit = 1;
    private bool isQuantityValid = true;

    /// <summary>Last valid fixed price in kopecks, for the same reason as <see cref="quantityPerUnit"/>.</summary>
    private long fixedPriceKopecks;
    private bool isFixedPriceValid = true;

    /// <summary>The exact value that will be written to <see cref="ComboComponent.ComponentPriceKopecks"/>.</summary>
    private long? componentPriceKopecks;

    /// <param name="id">
    /// The slot's identity. Preserved across an edit so the save matches the row it came from instead
    /// of deleting it and inserting a new one; <see cref="Guid.NewGuid"/> for a slot added here.
    /// </param>
    /// <param name="changed">Called after anything that can move the bundle's price.</param>
    public ComboSlotRow(
        Guid id,
        IEnumerable<CatalogOption<Product>> productOptions,
        IEnumerable<CatalogOption<Product>> substituteOptions,
        IEnumerable<ComboPriceModeOption> priceModes,
        Action changed)
    {
        Id = id;
        ProductOptions = [.. productOptions];
        SubstituteOptions = [.. substituteOptions];
        PriceModes = [.. priceModes];
        this.changed = changed;
    }

    /// <summary>The dish's name for the row, or a prompt while the slot is still empty.</summary>
    public string ProductName => Product?.Name ?? "Выберите блюдо";

    /// <summary>
    /// What this slot adds to the à la carte reference, formatted. A bundle takes a whole number of a
    /// dish, so this is the only figure the row needs — the operator is checking the total above, not
    /// multiplying out loud.
    /// </summary>
    public string LineTotalText => HasProduct
        ? $"{quantityPerUnit} × {TextFormat.Money(Money.FromKopecks(UnitKopecks))}"
        : string.Empty;

    /// <summary>Placeholder for the "своя цена" field, naming the selected currency's unit.</summary>
    public string FixedPricePlaceholder => $"Цена, {Currencies.Default.MinorUnitName}";

    public Guid Id { get; }

    public IReadOnlyList<CatalogOption<Product>> ProductOptions { get; }
    public IReadOnlyList<CatalogOption<Product>> SubstituteOptions { get; }
    public IReadOnlyList<ComboPriceModeOption> PriceModes { get; }

    // ── Dish ──────────────────────────────────────────────────────────────────────────────────

    public CatalogOption<Product>? SelectedProductOption
    {
        get => selectedProductOption;
        set { if (SetProperty(ref selectedProductOption, value)) Refresh(); }
    }

    public Product? Product => SelectedProductOption?.Value;
    public Guid? ProductId => Product?.Id;
    public bool HasProduct => Product is not null;

    // ── Multiplicity ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nudges the multiplicity by whole units, for the cart's stepper.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="QuantityText"/> rather than writing
    /// <see cref="QuantityPerUnit"/> directly. That is the whole point of the indirection: the setter is
    /// where validation, the warning text and the recomputed totals live, so a stepper that bypassed it
    /// would be a second place where quantity changes — and it would be the one with no guard. A bundle
    /// takes a whole number of a dish, never a fraction and never zero, which is why the floor is 1 and
    /// why <c>−</c> at 1 is refused rather than clamped: silently turning "−" into a no-op leaves the
    /// operator pressing a button that does nothing, and one that reaches 0 would multiply the component
    /// out of the bundle's own total.
    /// </remarks>
    public void StepQuantity(int delta)
    {
        var current = int.TryParse(QuantityText.Trim(), out var parsed) && parsed >= 1 ? parsed : 1;
        var next = current + delta;
        if (next < 1) return;

        QuantityText = next.ToString();
    }

    public string QuantityText
    {
        get => quantityText;
        set
        {
            if (!SetProperty(ref quantityText, value ?? string.Empty)) return;

            // Below 1 is refused by the domain, and 0 would silently multiply nothing away, so an
            // unusable entry holds the LAST VALID value instead of contributing 0 to the total. The
            // row says so in its warning and the save is blocked; a live figure that silently
            // dropped to a third would be the worse of the two.
            if (int.TryParse(quantityText.Trim(), out var parsed) && parsed >= 1)
            {
                if (!isQuantityValid)
                {
                    isQuantityValid = true;
                    OnPropertyChanged(nameof(IsQuantityValid));
                }

                if (parsed != quantityPerUnit)
                {
                    quantityPerUnit = parsed;
                    OnPropertyChanged(nameof(QuantityPerUnit));
                }
            }
            else if (isQuantityValid)
            {
                isQuantityValid = false;
                OnPropertyChanged(nameof(IsQuantityValid));
            }

            Refresh();
        }
    }

    public int QuantityPerUnit => quantityPerUnit;

    public bool IsQuantityValid => isQuantityValid;

    // ── What the slot is charged ──────────────────────────────────────────────────────────────

    public ComboPriceModeOption? SelectedPriceModeOption
    {
        get => selectedPriceModeOption;
        set
        {
            if (!SetProperty(ref selectedPriceModeOption, value)) return;
            ApplyPriceMode(value?.Mode ?? ComboPriceMode.DishPrice);
        }
    }

    public ComboPriceMode PriceMode => priceMode;

    /// <summary>Only a fixed slot has a price to type; the other two modes own their own figure.</summary>
    public bool IsFixedPrice => priceMode == ComboPriceMode.Fixed;

    public bool IsFixedPriceValid => isFixedPriceValid;

    public string FixedPriceText
    {
        get => fixedPriceText;
        set
        {
            if (!SetProperty(ref fixedPriceText, value ?? string.Empty)) return;
            RecomputeSlotPrice();
            Refresh();
        }
    }

    /// <summary>What will be stored. Null takes the dish price, 0 is free, a number is this price instead.</summary>
    public long? ComponentPriceKopecks => componentPriceKopecks;

    // ── Substitute ────────────────────────────────────────────────────────────────────────────

    public CatalogOption<Product>? SelectedSubstituteOption
    {
        get => selectedSubstituteOption;
        set { if (SetProperty(ref selectedSubstituteOption, value)) Refresh(); }
    }

    public Guid? SubstituteProductId => SelectedSubstituteOption?.Value?.Id;
    public bool HasSubstitute => SubstituteProductId is not null;

    // ── Derived figures ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What one of this dish contributes to the à la carte reference: the DISH's own price, always.
    /// </summary>
    /// <remarks>
    /// This used to be the slot's own price when it stated one, otherwise the dish's. The form no
    /// longer exposes a per-slot price, so that override became invisible while it still drove the
    /// arithmetic — the worst of both. The screen printed «Блюдо стоит 160.00 ₿» while the
    /// «по отдельноcти» figure was summed from a stored 120.00 ₿, and the discount the customer is
    /// quoted was computed from the invisible one. A number the operator cannot see and cannot change
    /// must not decide the saving, so the override is out of the calculation entirely.
    /// <para>
    /// THE SAVED VALUE IS STILL WRITTEN. <c>componentPriceKopecks</c> is preserved and BuildSlots
    /// still saves it, so nothing is destroyed and reverting this is a one-line change. It is dead
    /// weight, not a deleted field: the column still exists and the still-stored values are now
    /// ignored, which is the honest way to end it — dropping them is a migration nobody asked for.
    /// </para>
    /// <para>
    /// The bundle's CHARGE is unaffected either way: that is <c>Combo.PriceKopecks</c> and never came
    /// from here. This figure only ever measured the bundle against buying its parts separately.
    /// </para>
    /// </remarks>
    public long UnitKopecks => Product?.PriceKopecks ?? 0;

    /// <summary>What the slot contributes to one unit of the bundle.</summary>
    public long LineKopecks => (long)quantityPerUnit * UnitKopecks;

    /// <summary>The dish's own price, shown next to the slot price so the choice is a visible comparison.</summary>
    public string ProductPriceText => Product is { } product
        ? $"Блюдо стоит {TextFormat.Money(product.Price)}"
        : "Блюдо не выбрано";

    /// <summary>The chosen mode in words — the slot's price is derived, so it is stated, not typed.</summary>
    public string PriceModeSummary => priceMode switch
    {
        ComboPriceMode.Free => "Бесплатно",
        ComboPriceMode.Fixed => $"Своя цена: {TextFormat.Money(Money.FromKopecks(UnitKopecks))}",
        _ => Product is { } product ? $"Цена блюда: {TextFormat.Money(product.Price)}" : "Цена блюда"
    };

    /// <summary>The slot's own contribution, so the total above can be checked by eye.</summary>
    public string LineSummary =>
        $"{quantityPerUnit} × {TextFormat.Money(Money.FromKopecks(UnitKopecks))}"
        + $" = {TextFormat.Money(Money.FromKopecks(LineKopecks))}";

    /// <summary>The replacement, or an empty string when none is set. D365's rule: a dearer replacement reprices the bundle.</summary>
    public string SubstituteText => SelectedSubstituteOption?.Value is { } substitute
        ? $"Замена: {substitute.Name}"
        : "Замена не настроена — без блюда комбо не продать";

    /// <summary>
    /// What is wrong with this row, in words. Empty when nothing is. The save is refused on the same
    /// conditions, so this is a warning that appears where the mistake is rather than at the button.
    /// </summary>
    public string WarningText
    {
        get
        {
            var problems = new List<string>(2);
            if (!HasProduct) problems.Add("выберите блюдо");
            if (!isQuantityValid) problems.Add("количество должно быть целым числом не меньше 1");
            if (priceMode == ComboPriceMode.Fixed && !isFixedPriceValid) problems.Add("своя цена должна быть числом не меньше нуля");
            return problems.Count == 0 ? string.Empty : string.Join("; ", problems);
        }
    }

    /// <summary>
    /// Loads a saved slot into a fresh row. Assigns the backing fields and refreshes once, rather
    /// than going through the setters, so opening a bundle does not re-announce every member N times
    /// while nothing is bound to the row yet.
    /// </summary>
    public void Initialise(
        Guid? productId,
        Guid? substituteProductId,
        ComboPriceMode mode,
        int quantity,
        long? componentPrice)
    {
        selectedProductOption = ProductOptions.FirstOrDefault(option => option.Value?.Id == productId);
        selectedSubstituteOption = substituteProductId is null
            ? SubstituteOptions.FirstOrDefault(option => option.IsNone)
            : SubstituteOptions.FirstOrDefault(option => option.Value?.Id == substituteProductId);

        priceMode = mode;
        selectedPriceModeOption = PriceModes.FirstOrDefault(option => option.Mode == mode);
        OnPropertyChanged(nameof(PriceMode));
        OnPropertyChanged(nameof(IsFixedPrice));
        OnPropertyChanged(nameof(SelectedPriceModeOption));

        quantityPerUnit = Math.Max(1, quantity);
        quantityText = quantityPerUnit.ToString();
        OnPropertyChanged(nameof(QuantityPerUnit));
        OnPropertyChanged(nameof(QuantityText));

        fixedPriceKopecks = componentPrice ?? 0;
        // Shown even in the other two modes, so switching to "своя цена" does not blank the field the
        // manager is about to edit.
        fixedPriceText = Money.FromKopecks(fixedPriceKopecks).ToString("0.##");
        OnPropertyChanged(nameof(FixedPriceText));

        RecomputeSlotPrice();
        Refresh();
    }

    private void ApplyPriceMode(ComboPriceMode mode)
    {
        if (priceMode == mode) return;
        priceMode = mode;
        OnPropertyChanged(nameof(PriceMode));
        OnPropertyChanged(nameof(IsFixedPrice));

        // Seeding the fixed field with the dish's price is a convenience, not a decision: leaving it
        // untouched behaves exactly like «цена блюда», so nothing is silently written by switching
        // modes and looking again.
        if (mode == ComboPriceMode.Fixed && fixedPriceText.Length == 0 && Product is { } product)
            SetProperty(ref fixedPriceText, product.Price.ToString("0.##"), nameof(FixedPriceText));

        RecomputeSlotPrice();
        Refresh();
    }

    private void RecomputeSlotPrice()
    {
        var valid = true;
        if (priceMode == ComboPriceMode.Fixed)
        {
            if (TextFormat.TryParseDecimal(fixedPriceText.Trim(), out var amount) && amount >= 0)
                fixedPriceKopecks = Money.ToKopecks(amount);
            else
                valid = false;
        }

        if (valid != isFixedPriceValid)
        {
            isFixedPriceValid = valid;
            OnPropertyChanged(nameof(IsFixedPriceValid));
        }

        var price = priceMode switch
        {
            ComboPriceMode.DishPrice => (long?)null,
            ComboPriceMode.Free => 0L,
            _ => fixedPriceKopecks
        };

        if (price != componentPriceKopecks)
        {
            componentPriceKopecks = price;
            OnPropertyChanged(nameof(ComponentPriceKopecks));
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Product));
        OnPropertyChanged(nameof(ProductId));
        OnPropertyChanged(nameof(HasProduct));
        OnPropertyChanged(nameof(SubstituteProductId));
        OnPropertyChanged(nameof(HasSubstitute));
        OnPropertyChanged(nameof(UnitKopecks));
        OnPropertyChanged(nameof(LineKopecks));
        OnPropertyChanged(nameof(ProductPriceText));
        OnPropertyChanged(nameof(PriceModeSummary));
        OnPropertyChanged(nameof(LineSummary));
        OnPropertyChanged(nameof(SubstituteText));
        OnPropertyChanged(nameof(WarningText));

        // The cart-shaped row's own two figures. Refresh is the right place rather than each setter,
        // because both depend on the dish AND on the quantity: announcing them from the product setter
        // alone would leave the line total stale after a stepper press, which is the change an operator
        // makes most often.
        OnPropertyChanged(nameof(ProductName));
        OnPropertyChanged(nameof(LineTotalText));

        changed();
    }
}
