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
