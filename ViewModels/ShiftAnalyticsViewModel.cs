using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out of
// the change that added it; it belongs beside ResourceStyles.TryGetColor. See its own remarks.
using CafePosApp.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

/// <summary>One row of the shift picker.</summary>
/// <param name="Id">The shift to analyse.</param>
/// <param name="DisplayName">«Текущая» / «Завершённая» plus the local times.</param>
/// <param name="IsClosed">
/// Whether the shift has an end time. Carried as data rather than re-derived from the name because
/// the picker's default selection IS this flag: «Анализировать смену» opens on the shift still
/// running, and falls back to the newest closed one only when nothing is open.
/// </param>
public sealed record ShiftChoice(Guid Id, string DisplayName, bool IsClosed);

/// <summary>
/// One modifier line of the product breakdown, as it is bound on screen.
/// </summary>
/// <remarks>
/// An <see cref="ObservableObject"/> rather than the Core record, and the ONLY reason is the bindable
/// <see cref="IsVisible"/>: the projection already dropped the lines a filter hid, so there is no
/// per-line visibility logic here — the row is asked to disappear or not by the ViewModel when it
/// rebuilds, never by a row deciding something about itself.
/// </remarks>
public sealed partial class ProductAnalyticsLineRow : ObservableObject
{
    private bool isVisible = true;

    public ProductAnalyticsLineRow(ProductAnalyticsLine line)
    {
        ProductName = line.DishName;
        ModifierName = line.ModifierName;
        Quantity = line.Quantity;
        RevenueText = TextFormat.Money(line.Revenue);
        Hint = $"{line.DishName}, {line.ModifierName}: × {line.Quantity}, {RevenueText}";
    }

    public string ProductName { get; }

    /// <summary>The composed modifier/variant description, or «Без модификатора».</summary>
    public string ModifierName { get; }

    public int Quantity { get; }

    /// <summary>Money in the active currency, resolved when the row was built.</summary>
    public string RevenueText { get; }

    /// <summary>The whole line in one sentence, for a screen reader that cannot see the columns.</summary>
    public string Hint { get; }

    public bool IsVisible
    {
        get => isVisible;
        set => SetProperty(ref isVisible, value);
    }
}

/// <summary>
/// One dish header with its modifier rows under it, collapsible.
/// </summary>
/// <remarks>
/// The totals are of what the header CONTAINS, which the projection has already filtered: a dish
/// whose rows a search term removed reports only what is left. That is a deliberate difference from
/// the shift report, where the figure is the dish's whole shift — on a filtered screen the number
/// the reader adds up from the rows below must be the number on the header.
/// </remarks>
public sealed partial class ProductAnalyticsDishRow : ObservableObject
{
    public ProductAnalyticsDishRow(ProductAnalyticsDish dish, string share)
    {
        Name = dish.Name;
        Quantity = dish.Quantity;
        Revenue = dish.Revenue;
        Share = share;
        Lines = new ObservableCollection<ProductAnalyticsLineRow>(dish.Lines.Select(line => new ProductAnalyticsLineRow(line)));
    }

    public string Name { get; }

    public int Quantity { get; }

    public decimal Revenue { get; }

    /// <summary>«12% выручки смены», or empty at zero. Resolved at build — the denominator is fixed.</summary>
    public string Share { get; }

    public ObservableCollection<ProductAnalyticsLineRow> Lines { get; }

    private bool isExpanded = true;

    /// <summary>
    /// Whether the modifier lines are shown. OPEN by default: the shift breakdown is a handful of
    /// rows, and hiding content behind a tap the reader has to discover is the wrong default for a
    /// report. A filter or a re-sort does not change it — see
    /// <see cref="ShiftAnalyticsViewModel.RebuildProductRows"/> — while a SHIFT change folds
    /// everything, because there the dishes are different ones.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (!SetProperty(ref isExpanded, value)) return;
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(ChevronGlyph));
            OnPropertyChanged(nameof(Hint));
        }
    }

    /// <summary>The chevron's state, spelled out because a glyph is not a state.</summary>
    public bool IsCollapsed => !isExpanded;

    /// <summary>An up chevron while open, a right one when collapsed. A text glyph, not an asset.</summary>
    public string ChevronGlyph => isExpanded ? "⌃" : "›";

    public string QuantityText => $"× {Quantity}";

    public string RevenueText => TextFormat.Money(Revenue);

    /// <summary>
    /// The header as one sentence for a screen reader: what it is, how much, and whether it is
    /// open. Without the last part a non-sighted reader cannot tell a folded dish from a dish with
    /// nothing under it.
    /// </summary>
    public string Hint => $"{Name}. {QuantityText}, {RevenueText}. {Share}. {(isExpanded ? "Развёрнуто" : "Свёрнуто")}";
}

/// <summary>One section header with the dishes sold in it.</summary>
public sealed partial class ProductAnalyticsSectionRow : ObservableObject
{
    public ProductAnalyticsSectionRow(ProductAnalyticsSection section, Func<decimal, string> share)
    {
        Name = section.Name;
        Quantity = section.Quantity;
        Revenue = section.Revenue;
        Share = share(section.Revenue);
        Dishes = new ObservableCollection<ProductAnalyticsDishRow>(
            section.Dishes.Select(dish => new ProductAnalyticsDishRow(dish, share(dish.Revenue))));
    }

    public string Name { get; }

    public int Quantity { get; }

    public decimal Revenue { get; }

    /// <summary>«12% выручки смены» for the section, or empty when the shift sold nothing.</summary>
    public string Share { get; }

    public ObservableCollection<ProductAnalyticsDishRow> Dishes { get; }

    public string QuantityText => $"× {Quantity}";

    public string RevenueText => TextFormat.Money(Revenue);

    public string Hint => $"{Name}. {QuantityText}, {RevenueText}. {Share}";
}

/// <summary>
/// One chip of the section filter strip: «Все» plus one chip per section present in the shift.
/// </summary>
/// <remarks>
/// The count is on the chip because deciding where to look is an arithmetic question, and it is
/// text rather than a badge so a screen reader reads it with the name — the same reasoning as
/// <see cref="OrderFilterChip"/>, which this mirrors.
/// </remarks>
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

/// <summary>One choice of the sorting control: a criterion, spelled out.</summary>
public sealed partial class ProductAnalyticsSortOption : ObservableObject
{
    public ProductAnalyticsSortOption(ProductAnalyticsSortCriterion criterion, string name)
    {
        Criterion = criterion;
        Name = name;
    }

    public ProductAnalyticsSortCriterion Criterion { get; }

    public string Name { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!SetProperty(ref isSelected, value)) OnPropertyChanged(nameof(Hint));
        }
    }

    public string Hint => $"Сортировать: {Name}{(IsSelected ? ". Выбрано" : string.Empty)}";
}

/// <summary>One choice of the grouping control.</summary>
public sealed partial class ProductAnalyticsGroupingOption : ObservableObject
{
    public ProductAnalyticsGroupingOption(ProductAnalyticsGrouping grouping, string name)
    {
        Grouping = grouping;
        Name = name;
    }

    public ProductAnalyticsGrouping Grouping { get; }

    public string Name { get; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!SetProperty(ref isSelected, value)) OnPropertyChanged(nameof(Hint));
        }
    }

    public string Hint => $"Группировать: {Name}{(IsSelected ? ". Выбрано" : string.Empty)}";
}

public partial class ShiftAnalyticsViewModel : ObservableObject
{
    private readonly IOrderService orders;
    private readonly ILogger<ShiftAnalyticsViewModel> logger;

    private double averagePreparationMinutes;
    private double averageCompletionMinutes;
    private CashReconciliation? reconciliation;

    public ShiftAnalyticsViewModel(IOrderService orders, ILogger<ShiftAnalyticsViewModel> logger)
    {
        this.orders = orders;
        this.logger = logger;
        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        ToggleFilterPanelCommand = new RelayCommand(() => IsFilterPanelOpen = !IsFilterPanelOpen);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        SelectSectionChipCommand = new RelayCommand<ProductAnalyticsSectionChip>(ToggleSectionChip);
        SelectSortCommand = new RelayCommand<ProductAnalyticsSortOption>(SelectSort);
        SelectGroupingCommand = new RelayCommand<ProductAnalyticsGroupingOption>(SelectGrouping);
        ToggleSortDirectionCommand = new RelayCommand(SortDirectionCommand);
        ToggleDishCommand = new RelayCommand<ProductAnalyticsDishRow>(ToggleDish);
        SeedControls();
    }

    public ObservableCollection<ShiftChoice> Shifts { get; } = [];

    // ── The product breakdown: three shapes, one at a time ───────────────────────────────────
    // The projection returns whichever tree the grouping asks for, and only that one is bound.
    // Three collections rather than one union type, because a BindableLayout has no way to pick a
    // DataTemplate by runtime type — a single list would mean one template rendering three
    // different things behind IsVisible panels.
    public ObservableCollection<ProductAnalyticsSectionRow> SectionRows { get; } = [];
    public ObservableCollection<ProductAnalyticsDishRow> DishRows { get; } = [];
    public ObservableCollection<ProductAnalyticsLineRow> LineRows { get; } = [];

    // ── The controls ────────────────────────────────────────────────────────────────────────
    public ObservableCollection<ProductAnalyticsSectionChip> SectionChips { get; } = [];
    public ObservableCollection<ProductAnalyticsSortOption> SortOptions { get; } = [];
    public ObservableCollection<ProductAnalyticsGroupingOption> GroupingOptions { get; } = [];

    private bool isFilterPanelOpen;

    /// <summary>
    /// Whether the filter/sort panel is open. CLOSED by default: it is five controls and a search
    /// box, and a manager opening the page to read the shift does not want them between the cards
    /// and the breakdown. The trigger carries the current state in words, so a collapsed panel is
    /// never "what is this list showing" — see <see cref="FilterPanelSummary"/>.
    /// </summary>
    public bool IsFilterPanelOpen
    {
        get => isFilterPanelOpen;
        set
        {
            if (!SetProperty(ref isFilterPanelOpen, value)) return;
            OnPropertyChanged(nameof(FilterPanelSummary));
            OnPropertyChanged(nameof(FilterPanelHint));
            OnPropertyChanged(nameof(FilterPanelGlyph));
        }
    }

    /// <summary>The chevron on the panel trigger.</summary>
    public string FilterPanelGlyph => isFilterPanelOpen ? "⌃" : "›";

    private string searchText = string.Empty;

    /// <summary>
    /// The search term, bound two ways to an <c>Entry</c>. Set from code by
    /// <see cref="ClearSearchCommand"/> as well as by the user, so it raises rather than being a
    /// field the view writes into.
    /// </summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(FilterPanelSummary));
            OnPropertyChanged(nameof(FilterPanelHint));

            // Re-projected on every keystroke, which is affordable ONLY because the rows are already
            // in hand and nothing here touches the database — see ProductAnalyticsProjection. The
            // alternative, waiting for a submit, makes a filter feel like it ignored the first
            // letters.
            RebuildProductRows();
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    // The five commands behind the filter panel and the collapsible dish headers. Declared HERE, on the
    // page's own type, rather than in the List partial: a compiled binding on the page resolves
    // against ShiftAnalyticsViewModel, and a property the compiler cannot see on that type is a
    // build failure rather than a silently unbound tap.
    public IRelayCommand ToggleFilterPanelCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IRelayCommand<ProductAnalyticsSectionChip> SelectSectionChipCommand { get; }

    public IRelayCommand<ProductAnalyticsSortOption> SelectSortCommand { get; }

    public IRelayCommand<ProductAnalyticsGroupingOption> SelectGroupingCommand { get; }

    public IRelayCommand ToggleSortDirectionCommand { get; }

    public IRelayCommand<ProductAnalyticsDishRow> ToggleDishCommand { get; }

    private bool hasNoProductRows;

    /// <summary>
    /// True while the list has nothing to show. Split three ways by <see cref="EmptyListText"/>,
    /// because "the shift sold nothing", "the filter excluded everything" and "the search found
    /// nothing" are three different facts and the one sentence that used to sit here could only
    /// state the first of them.
    /// </summary>
    public bool HasNoProductRows
    {
        get => hasNoProductRows;
        private set => SetProperty(ref hasNoProductRows, value);
    }

    /// <summary>
    /// The list is empty because a filter or a search hid everything — which is a different fact
    /// from a shift with no closed orders, and is what the second empty state has to be keyed on.
    /// </summary>
    public bool IsFilteredToNothing { get; private set; }

    /// <summary>«По названию ничего не найдено.»</summary>
    public bool IsSearchEmpty => IsFilteredToNothing && HasSearch;

    public string EmptyListText => IsSearchEmpty
        ? $"По запросу «{SearchText.Trim()}» ничего не найдено."
        : IsFilteredToNothing
            ? "Выбранные фильтры ничего не оставили."
            : "За выбранную смену нет закрытых заказов.";

    private ShiftChoice? selectedShift;

    /// <summary>
    /// The shift the page is about. Setting it RE-RUNS the analysis.
    /// </summary>
    /// <remarks>
    /// The setter is where the analysis used to be missing, and it was not a small gap: picking
    /// another shift in the picker changed the label at the top and NOTHING else. The cards and the
    /// breakdown kept the previous shift's figures, so the page said one shift's name over another
    /// shift's revenue — verified on the emulator, where «Завершённая · 30.09» sat on top of a
    /// «Продано позиций 30» that belonged to a different shift entirely, and «Обновить аналитику»
    /// existed only to paper over it. The whole page is ABOUT one shift, so its selection is an
    /// input to the page, not a display preference.
    /// <para>
    /// Guarded against self-invocation: <see cref="LoadAsync"/> assigns this while restoring the
    /// previous selection, and the analysis it then runs must not start a second one.
    /// </para>
    /// </remarks>
    public ShiftChoice? SelectedShift
    {
        get => selectedShift;
        set
        {
            if (!SetProperty(ref selectedShift, value)) return;
            if (suppressShiftReload) return;

            _ = AnalyzeAsync();
        }
    }

    /// <summary>
    /// Set while <see cref="LoadAsync"/> picks the shift to open on, so restoring a selection does
    /// not fire a second analysis on top of the one that method is about to run itself.
    /// </summary>
    private bool suppressShiftReload;

    private decimal revenue;
    public decimal Revenue { get => revenue; private set => SetProperty(ref revenue, value); }

    private int ordersCount;
    public int OrdersCount { get => ordersCount; private set => SetProperty(ref ordersCount, value); }

    private int cancelledOrdersCount;
    public int CancelledOrdersCount { get => cancelledOrdersCount; private set => SetProperty(ref cancelledOrdersCount, value); }

    private int allOrdersCount;
    public int AllOrdersCount { get => allOrdersCount; private set => SetProperty(ref allOrdersCount, value); }

    private decimal averageCheck;
    public decimal AverageCheck { get => averageCheck; private set => SetProperty(ref averageCheck, value); }

    private int itemsCount;
    public int ItemsCount { get => itemsCount; private set => SetProperty(ref itemsCount, value); }

    // ── Money through the till ────────────────────────────────────────────────────────────────
    // The two halves the shift's revenue is made of, so that the reconciled figure has a company.
    // The cash side is the same countable number the shift report counts out and the shift close
    // compares against, taken from ShiftStats.ExpectedCashNow rather than subtracted here — one
    // definition of "what is in the drawer", computed where both halves already exist. This page
    // is the only one that can read a CLOSED shift, so a manager reading yesterday's totals
    // anywhere else in the app has no way to see the cash that was actually reconciled.

    private decimal cashInDrawer;
    public decimal CashInDrawer
    {
        get => cashInDrawer;
        // Raises the staleness note as well, because its sentence quotes THIS figure. The frozen
        // count is a record, so re-reading the same shift reports it as equal and publishes nothing —
        // and a refund taken against a closed shift moves the live figure while the snapshot stays
        // exactly where it is. Without this the note would go on quoting the figure from the
        // previous load, which is the exact failure the note exists to report.
        private set { if (SetProperty(ref cashInDrawer, value)) NotifyStale(); }
    }

    /// <summary>Change put into the drawer over the shift, net of corrections.</summary>
    private decimal floatCash;
    public decimal FloatCash
    {
        get => floatCash;
        // Raises the formatted twin, because THAT is what the card binds: the page shows
        // FloatCashText, so a raise of FloatCash alone reaches no binding at all. These two setters
        // used to raise only themselves, and the texts were republished from NotifyStale instead —
        // which runs from the CashInDrawer setter, three lines EARLIER in AnalyzeAsync, while these
        // fields are still zero. So the card showed «Размен: 0.00 ₿» and «Изъято: 0.00 ₿» under a
        // drawer total that was itself correct, and kept showing them until the values happened to
        // return to zero. Found on device, not by a test: the total is bound to CashInDrawerText and
        // was right, so the page looked healthy.
        private set { if (SetProperty(ref floatCash, value)) OnPropertyChanged(nameof(FloatCashText)); }
    }

    /// <summary>Cash carried out of the drawer over the shift, net of corrections.</summary>
    private decimal payoutCash;
    public decimal PayoutCash
    {
        get => payoutCash;
        private set { if (SetProperty(ref payoutCash, value)) OnPropertyChanged(nameof(PayoutCashText)); }
    }

    private decimal cardTotal;
    public decimal CardTotal
    {
        get => cardTotal;
        private set
        {
            if (SetProperty(ref cardTotal, value))
            {
                NotifyCardComposition();
                // The card showing this figure binds CardTotalText, and NotifyCardComposition only
                // refreshes the caption sentence, so the amount itself needs its own raise.
                OnPropertyChanged(nameof(CardTotalText));
            }
        }
    }

    private decimal refundsCard;

    /// <summary>
    /// What was knocked back out of the card figure, shown only when there was any.
    /// </summary>
    /// <remarks>
    /// Core names one net figure — <c>ExpectedCashNow</c> — and no net card figure, because card
    /// money is not physically countable and there is nothing to count it against. The net is
    /// derived here, in one place, and the caption says which part of it came back, so the cell
    /// cannot be read as a gross "how much we took" and quietly disagree with the shift report's
    /// «Принято картой». It is hidden at zero: a caption with nothing to add would make this one
    /// card taller than the eight around it for no information.
    /// </remarks>
    public string CardRefundsText => refundsCard <= 0
        ? string.Empty
        : $"возвращено {TextFormat.Money(refundsCard)}";

    public bool HasCardRefunds => refundsCard > 0;

    private void NotifyCardComposition()
    {
        OnPropertyChanged(nameof(CardRefundsText));
        OnPropertyChanged(nameof(HasCardRefunds));
    }

    public string AveragePreparationTime => TextFormat.Duration(averagePreparationMinutes);
    public string AverageCompletionTime => TextFormat.Duration(averageCompletionMinutes);

    // "нет данных", not a bare dash: the duration cards on the same screen (Среднее
    // приготовление, Среднее выполнение) already say exactly that, and a lone "—" beside two
    // worded empty states read as a missing value rather than a measured absence.
    private string peakHour = "нет данных";
    public string PeakHour { get => peakHour; private set => SetProperty(ref peakHour, value); }

    // ── The end-of-shift cash count ───────────────────────────────────────────────────────────
    // The shift report CANNOT show this block: it rebinds to the new shift after a close and its
    // route carries no shift id, so it can only ever display the open shift, which by definition
    // has no count. This page is the one screen that can read a closed shift, so it is the only
    // place the reconciliation can be read after the fact.
    //
    // The Russian wording is HERE rather than in Core on purpose: Core exposes the enum and the
    // signed value and stops there, because a domain that ships sentences stops being one. See
    // CashWording for why the direction lives in the word and the number is printed unsigned.

    /// <summary>True when the selected shift was counted at all. Drives the whole card's visibility.</summary>
    public bool HasReconciliation => Reconciliation is not null;

    /// <summary>
    /// The recorded count, or null for a shift that was never counted.
    /// </summary>
    /// <remarks>
    /// A computed property would have been shorter, but the notifications it drives are the point:
    /// every line of the card is derived from the same record, and a card that shows the frozen
    /// expectation next to a difference computed from a previous selection is worse than no card.
    /// </remarks>
    public CashReconciliation? Reconciliation
    {
        get => reconciliation;
        private set
        {
            if (!SetProperty(ref reconciliation, value)) return;
            OnPropertyChanged(nameof(HasReconciliation));
            OnPropertyChanged(nameof(CountedAtText));
            OnPropertyChanged(nameof(ExpectedAtCloseText));
            OnPropertyChanged(nameof(CountedText));
            OnPropertyChanged(nameof(DifferenceText));
            OnPropertyChanged(nameof(HasDiscrepancy));
            OnPropertyChanged(nameof(DifferenceColor));
            OnPropertyChanged(nameof(ReasonText));
            OnPropertyChanged(nameof(HasReason));
            OnPropertyChanged(nameof(DifferenceHint));
            // The staleness sentence quotes the frozen figures as well as the live one, so it is
            // rebuilt whenever the record itself changes.
            NotifyStale();
        }
    }

    /// <summary>When the count became part of the record, in local time.</summary>
    public string CountedAtText => Reconciliation is null
        ? string.Empty
        : $"Пересчёт зафиксирован {Reconciliation.CountedAt.ToLocalTime():dd.MM.yyyy HH:mm}";

    /// <summary>The frozen expectation, always WITH its moment.</summary>
    public string ExpectedAtCloseText => Reconciliation is null
        ? string.Empty
        : $"Ожидалось на момент закрытия: {TextFormat.Money(Money.FromKopecks(Reconciliation.ExpectedKopecks))}";

    public string CountedText => Reconciliation is null
        ? string.Empty
        : $"Пересчитано в кассе: {TextFormat.Money(Money.FromKopecks(Reconciliation.CountedKopecks))}";

    /// <summary>«Сходится» / «Не хватает 240,00 ₽» / «Лишние 240,00 ₽». Never a signed number alone.</summary>
    public string DifferenceText => Reconciliation is null
        ? string.Empty
        : CashWording.Describe(Reconciliation.DiscrepancyKopecks);

    /// <summary>
    /// Drives the tint on <see cref="DifferenceText"/>. The word is the primary signal and this is
    /// only the third one, so the three states stay readable in greyscale.
    /// </summary>
    public bool HasDiscrepancy => Reconciliation is { Difference: not CashDifference.Matched };

    /// <summary>
    /// The ink for <see cref="DifferenceText"/>: the danger tone when the drawer does not agree,
    /// and the label's own default ink otherwise.
    /// </summary>
    /// <remarks>
    /// The tint used to arrive as a <c>DataTrigger</c> Setter holding a bare
    /// <c>{StaticResource Danger}</c>. A trigger's Setter takes a VALUE, not a binding expression,
    /// so the token was resolved once at parse time and stayed on the light palette in every theme:
    /// Danger <c>#D32F2F</c> on <c>BackgroundDark #121212</c> is 3.76:1, where DangerDark
    /// <c>#FF9E8E</c> on the same surface is 9.40:1. Resolved here instead, against the live theme.
    /// <para>
    /// The untinted branch has to NAME a colour rather than defer to the implicit
    /// <c>Style TargetType="Label"</c>, because a local binding suppresses the style's own setter:
    /// <c>Resolve("Black", "White")</c> reproduces exactly what that style declares
    /// (<c>AppThemeBinding Light=Black, Dark=White</c>, Resources/Styles/Styles.xaml), so the
    /// balanced case looks identical to before. If that implicit style's TextColor ever changes,
    /// this property has to change with it — that is the cost of moving a trigger into a
    /// ViewModel, and it is recorded here rather than left to be discovered.
    /// </para>
    /// <para>
    /// Nothing here is colour-only. <see cref="DifferenceText"/> spells the direction out in words
    /// and <see cref="DifferenceHint"/> repeats it for a screen reader, which cannot see a tint.
    /// </para>
    /// </remarks>
    public Color DifferenceColor => HasDiscrepancy
        ? ThemeColors.Resolve("Danger", "DangerDark")
        : ThemeColors.Resolve("Black", "White");

    public string ReasonText => Reconciliation?.Reason ?? string.Empty;
    public bool HasReason => !string.IsNullOrWhiteSpace(Reconciliation?.Reason);

    /// <summary>
    /// The same three states as one phrase for a screen reader, which cannot see the tint and has
    /// no way to know that "−240,00" is a shortage rather than a balance.
    /// </summary>
    public string DifferenceHint => Reconciliation is null
        ? string.Empty
        : $"Пересчёт кассы: {CashWording.Describe(Reconciliation.DiscrepancyKopecks)}";

    // ── Staleness ─────────────────────────────────────────────────────────────────────────────
    // "Did the cashier err at close" and "are the books right now" are two different questions and
    // they have different answers on purpose: a refund taken against a CLOSED shift moves the live
    // drawer figure and leaves the frozen count alone, because the money physically left a drawer
    // that shift owned. The count is not rewritten, and the drift is shown rather than hidden.

    private bool reconciliationStale;
    public bool IsReconciliationStale
    {
        get => reconciliationStale;
        private set { if (SetProperty(ref reconciliationStale, value)) NotifyStale(); }
    }

    private void NotifyStale()
    {
        OnPropertyChanged(nameof(StaleText));
        OnPropertyChanged(nameof(StaleHint));
        // StaleText and StaleHint quote CashInDrawer in words, so the card that shows that same
        // figure as an amount has to be republished with it — otherwise the sentence updates and
        // the number beside it stays on the previous load's value.
        OnPropertyChanged(nameof(CashInDrawerText));
        // FloatCashText and PayoutCashText used to be raised here too, on the reasoning that this is
        // where every derived amount gets republished. It is the wrong place and was the cause of a
        // wrong figure on screen: this method runs from the CashInDrawer setter, which AnalyzeAsync
        // reaches BEFORE assigning FloatCash and PayoutCash, so it recomputed both texts from a pair
        // of still-zero fields and the card froze on «0.00 ₿» under a correct total. Each setter now
        // raises its own twin, where the value is known to be current.
    }

    // ── Formatted money ───────────────────────────────────────────────────────────────────────
    // The four headline cards were bound as StringFormat="{0:F2} ₽", fixed at XAML parse time and
    // therefore locked to a ruble sign with two decimals. Formatting moves here so it can read the
    // operator's selected currency; the cards keep their own Russian captions.

    /// <summary>Revenue in the active currency, e.g. "12 480,00 ₿".</summary>
    public string RevenueText => TextFormat.Money(Revenue);

    /// <summary>Average check in the active currency.</summary>
    public string AverageCheckText => TextFormat.Money(AverageCheck);

    /// <summary>Cash in the drawer in the active currency, e.g. "3 200,00 ₿".</summary>
    /// <remarks>
    /// Re-raised from <see cref="NotifyStale"/> rather than from its own setter, because the
    /// staleness note quotes this same figure in words — see that method.
    /// </remarks>
    public string CashInDrawerText => TextFormat.Money(CashInDrawer);

    /// <summary>Change put in, in the active currency.</summary>
    public string FloatCashText => TextFormat.Money(FloatCash);

    /// <summary>Cash carried out, in the active currency.</summary>
    public string PayoutCashText => TextFormat.Money(PayoutCash);

    /// <summary>Card money net of refunds, in the active currency.</summary>
    public string CardTotalText => TextFormat.Money(CardTotal);

    /// <summary>Re-raises the revenue and average-check figures, which have no dependent sentence.</summary>
    private void NotifyRevenueTexts()
    {
        OnPropertyChanged(nameof(RevenueText));
        OnPropertyChanged(nameof(AverageCheckText));
    }

    /// <summary>
    /// Both figures and the moment, in one sentence. Self-contained on purpose: a drift note that
    /// only said "the ledger moved" leaves the reader to reconstruct the two numbers from the lines
    /// above it.
    /// </summary>
    public string StaleText => Reconciliation is not { } count
        ? string.Empty
        : $"Учёт изменился после пересчёта (возврат по уже закрытой смене). "
          + $"На момент пересчёта {count.CountedAt.ToLocalTime():dd.MM HH:mm} было "
          + $"{TextFormat.Money(Money.FromKopecks(count.ExpectedKopecks))}, "
          + $"сейчас по учёту {TextFormat.Money(CashInDrawer)}.";

    public string StaleHint => Reconciliation is not { } count
        ? string.Empty
        : $"Учёт изменился после пересчёта. На момент пересчёта {count.CountedAt.ToLocalTime():dd.MM.yyyy HH:mm} "
          + $"в кассе было {TextFormat.Money(Money.FromKopecks(count.ExpectedKopecks))}, "
          + $"сейчас по учёту {TextFormat.Money(CashInDrawer)}. "
          + $"Сам пересчёт: {CashWording.Describe(count.DiscrepancyKopecks)}.";

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand AnalyzeCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var previousId = SelectedShift?.Id;
            var shifts = await orders.GetShiftsAsync();

            Shifts.SyncWith(
                shifts.Select(shift => new ShiftChoice(shift.Id,
                    $"{(shift.IsActive ? "Текущая" : "Завершённая")} · {shift.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}" +
                    $"{(shift.EndTime.HasValue ? $" – {shift.EndTime.Value.ToLocalTime():HH:mm}" : string.Empty)}",
                    shift.EndTime.HasValue)),
                choice => choice.Id);

            // THE DEFAULT IS THE SHIFT THAT IS OPEN RIGHT NOW — the owner's instruction, and it
            // reverses the previous "prefer a closed shift" default. The reasoning for the reversal
            // is that the closed-first rule fixed a real case and created a larger one: during a
            // shift, the number an operator actually wants is today's live hour, and a page that
            // opens on yesterday forces a manual pick every single time.
            //
            // WHAT THE REVERSAL COSTS, stated plainly rather than discovered later. The old comment
            // recorded a genuine complaint: an operator who closes a shift, taps «Анализировать
            // смену» to check the count landed, and is shown an empty hour. That regresses — but only
            // on FIRST open after a close. The common path is covered by `previousId`: the page
            // holds the open shift while the shift runs, so closing it and coming back keeps the
            // same selection, now marked «Завершённая». Shell keeps one page per tab, so this
            // ViewModel instance usually survives the close. The uncovered case is a cold open of
            // this tab straight after closing, which shows the new shift rather than the old one.
            //
            // EndTime is the closed test rather than !IsActive, because the domain's own definition
            // of "this shift ended" is the moment it ended, not a flag that happened to be flipped
            // at the time — the same reasoning as before, and it is what makes "is it open now" a
            // question about the data instead of about bookkeeping.
            var next = Shifts.FirstOrDefault(choice => choice.Id == previousId)
                ?? Shifts.FirstOrDefault(choice => !choice.IsClosed)
                ?? Shifts.FirstOrDefault(choice => choice.IsClosed)
                ?? Shifts.FirstOrDefault();
            var switched = next?.Id != previousId;

            // Folds are per-SHIFT state, not per-session state: a manager who folded away «Капучино» to
            // read the rest of the day does not expect the fold to survive moving to yesterday,
            // where it would hide a dish they have never seen folded. OPEN, not folded, is the
            // reset: the previous shift's folds are meaningless here.
            if (switched) OpenAllDishes();

            // Assigned with the reload suppressed: the analysis this method runs next IS the analysis
            // for this selection, and letting the setter start one too would run the two queries
            // concurrently against a DbContext each — harmless in result, wasteful, and a race on
            // whichever finished last.
            suppressShiftReload = true;
            try
            {
                SelectedShift = next;
            }
            finally
            {
                suppressShiftReload = false;
            }

            await AnalyzeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось загрузить аналитику");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AnalyzeAsync()
    {
        if (SelectedShift is null) return;

        try
        {
            var stats = await orders.GetShiftStatsAsync(SelectedShift.Id);
            AllOrdersCount = stats.AllOrdersCount;
            CancelledOrdersCount = stats.CancelledCount;
            Revenue = stats.Revenue;
            OrdersCount = stats.CompletedCount;
            AverageCheck = stats.AverageCheck;
            // Both are assigned above in the same pass; their formatted twins are separate bindings.
            NotifyRevenueTexts();
            ItemsCount = stats.ItemsCount;
            averagePreparationMinutes = stats.AveragePreparationMinutes;
            averageCompletionMinutes = stats.AverageCompletionMinutes;
            PeakHour = stats.PeakHour;

            // The same one definition the shift report and the CSV export read. Never PaymentsCash −
            // RefundsCash written here: a second definition of the drawer is how two reports end up
            // disagreeing about whether the till balanced.
            CashInDrawer = stats.ExpectedCashNow;

            // Assigned before the formatted texts are raised below, so the breakdown under the total
            // lands in the same pass as the total it explains.
            FloatCash = stats.FloatCash;
            PayoutCash = stats.PayoutCash;
            // The card side has no Core counterpart and needs none — card money is not countable, so
            // nothing is ever compared against a physical count of it. Derived once, here, and the
            // refunds that produced it are published alongside so the number is never bare.
            refundsCard = stats.RefundsCard;
            CardTotal = Money.Round(stats.PaymentsCard - stats.RefundsCard);
            // Raised again on purpose: the caption is built from BOTH halves, so it has to refresh
            // when the refunds move even if the net lands on the same value — an unchanged CardTotal
            // means the setter above raised nothing.
            NotifyCardComposition();

            // ORDERING: the staleness sentence is built from all three of these, and each setter
            // republishes it. CashInDrawer goes first and Reconciliation last, so the last raise
            // sees the current live figure and the current frozen one together — the reverse order
            // would publish a sentence quoting the previous shift's count against this shift's
            // drawer for as long as the bindings took to settle.
            Reconciliation = stats.Reconciliation;
            IsReconciliationStale = stats.IsReconciliationStale;

            OnPropertyChanged(nameof(AveragePreparationTime));
            OnPropertyChanged(nameof(AverageCompletionTime));

            ApplyAnalytics(await orders.GetProductAnalyticsAsync(SelectedShift.Id));

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build the shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось построить аналитику");
        }
    }
}