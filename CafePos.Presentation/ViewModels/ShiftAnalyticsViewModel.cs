using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

/// <summary>The shift report: preparation times, the cash reconciliation and the product breakdown.</summary>
public partial class ShiftAnalyticsViewModel : ObservableObject
{
    // Why two ports, and why these names: docs/decisions/analytics.md
    private readonly IOrderReporting reporting;
    private readonly IShiftLedger ledger;
    private readonly ILogger<ShiftAnalyticsViewModel> logger;

    private double averagePreparationMinutes;
    private double averageCompletionMinutes;
    private CashReconciliation? reconciliation;

    public ShiftAnalyticsViewModel(
        IOrderReporting reporting,
        IShiftLedger ledger,
        ILogger<ShiftAnalyticsViewModel> logger)
    {
        this.reporting = reporting;
        this.ledger = ledger;
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

    /// <summary>── The product breakdown: three shapes, one at a time ─────────────────────────────────── The projection returns whichever tree the grouping asks for, and only...</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    public ObservableCollection<ProductAnalyticsSectionRow> SectionRows { get; } = [];
    public ObservableCollection<ProductAnalyticsDishRow> DishRows { get; } = [];
    public ObservableCollection<ProductAnalyticsLineRow> LineRows { get; } = [];

    // ── The controls ────────────────────────────────────────────────────────────────────────
    public ObservableCollection<ProductAnalyticsSectionChip> SectionChips { get; } = [];
    public ObservableCollection<ProductAnalyticsSortOption> SortOptions { get; } = [];
    public ObservableCollection<ProductAnalyticsGroupingOption> GroupingOptions { get; } = [];

    private bool isFilterPanelOpen;

    /// <summary>Whether the filter/sort panel is open.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

    /// <summary>The search term, bound two ways to an Entry. Set from code by as well as by the user, so it raises rather than being a field the view writes into.</summary>

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(FilterPanelSummary));
            OnPropertyChanged(nameof(FilterPanelHint));

            /// <summary>Re-projected on every keystroke, which is affordable ONLY because the rows are already in hand and nothing here touches the database — see ProductAnalyticsProje...</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            RebuildProductRows();
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>The five commands behind the filter panel and the collapsible dish headers.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    public IRelayCommand ToggleFilterPanelCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IRelayCommand<ProductAnalyticsSectionChip> SelectSectionChipCommand { get; }

    public IRelayCommand<ProductAnalyticsSortOption> SelectSortCommand { get; }

    public IRelayCommand<ProductAnalyticsGroupingOption> SelectGroupingCommand { get; }

    public IRelayCommand ToggleSortDirectionCommand { get; }

    public IRelayCommand<ProductAnalyticsDishRow> ToggleDishCommand { get; }

    private bool hasNoProductRows;

    /// <summary>True while the list has nothing to show.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    public bool HasNoProductRows
    {
        get => hasNoProductRows;
        private set => SetProperty(ref hasNoProductRows, value);
    }

    /// <summary>    /// <summary>The list is empty because a filter or a search hid everything — which is a different factfrom a shift with no closed orders, and is w...</summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>from a shift with no closed orders, and is what the second empty state...</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    public bool IsFilteredToNothing { get; private set; }

    /// <summary>«По названию ничего не найдено.»</summary>
    public bool IsSearchEmpty => IsFilteredToNothing && HasSearch;

    public string EmptyListText => IsSearchEmpty
        ? $"По запросу «{SearchText.Trim()}» ничего не найдено."
        : IsFilteredToNothing
            ? "Выбранные фильтры ничего не оставили."
            : "За выбранную смену нет закрытых заказов.";

    private ShiftChoice? selectedShift;

    /// <summary>The shift the page is about. Setting it RE-RUNS the analysis.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

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

    /// <summary>Set while picks the shift to open on, so restoring a selection does not fire a second analysis on top of the one that method is about to run itself.</summary>

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

    /// <summary>── Money through the till ──────────────────────────────────────────────────────────────── The two halves the shift's revenue is made of, so that the reconciled...</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>


    private decimal cashInDrawer;
    public decimal CashInDrawer
    {
        get => cashInDrawer;
        /// <summary>Raises the staleness note as well, because its sentence quotes THIS figure.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        private set { if (SetProperty(ref cashInDrawer, value)) NotifyStale(); }
    }

    /// <summary>Change put into the drawer over the shift, net of corrections.</summary>
    private decimal floatCash;
    public decimal FloatCash
    {
        get => floatCash;
        /// <summary>Raises the formatted twin, because THAT is what the card binds: the page shows FloatCashText, so a raise of FloatCash alone reaches no binding at all.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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

    /// <summary>What was knocked back out of the card figure, shown only when there was any.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

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

    /// <summary>── The end-of-shift cash count ─────────────────────────────────────────────────────────── The shift report CANNOT show this block: it rebinds to the new shift...</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>


    /// <summary>True when the selected shift was counted at all. Drives the whole card's visibility.</summary>
    public bool HasReconciliation => Reconciliation is not null;

    /// <summary>The recorded count, or null for a shift that was never counted.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

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

    /// <summary>Drives the tint on . The word is the primary signal and this is only the third one, so the three states stay readable in greyscale.</summary>

    public bool HasDiscrepancy => Reconciliation is { Difference: not CashDifference.Matched };

    /// <summary>The ink for : the danger tone when the drawer does not agree, and the label's own default ink otherwise.</summary>
    /// <remarks>Почему так — `docs/decisions/analytics.md`</remarks>

    public Color DifferenceColor => HasDiscrepancy
        ? PaletteAccess.Resolve("Danger", "DangerDark")
        : PaletteAccess.Resolve("Black", "White");

    public string ReasonText => Reconciliation?.Reason ?? string.Empty;
    public bool HasReason => !string.IsNullOrWhiteSpace(Reconciliation?.Reason);

    /// <summary>The same three states as one phrase for a screen reader, which cannot see the tint and has no way to know that "−240,00" is a shortage rather than a balance.</summary>

    public string DifferenceHint => Reconciliation is null
        ? string.Empty
        : $"Пересчёт кассы: {CashWording.Describe(Reconciliation.DiscrepancyKopecks)}";

    /// <summary>── Staleness ───────────────────────────────────────────────────────────────────────────── "Did the cashier err at close" and "are the books right now" are two...</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>


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
        /// <summary>FloatCashText and PayoutCashText used to be raised here too, on the reasoning that this is where every derived amount gets republished.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    }

    /// <summary>── Formatted money ─────────────────────────────────────────────────────────────────────── The four headline cards were bound as StringFormat="{0:F2} ₽", fixed...</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>


    /// <summary>Revenue in the active currency, e.g. "12 480,00 ₿".</summary>
    public string RevenueText => TextFormat.Money(Revenue);

    /// <summary>Average check in the active currency.</summary>
    public string AverageCheckText => TextFormat.Money(AverageCheck);

    /// <remarks>`docs/decisions/analytics.md`</remarks>

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

    /// <summary>Both figures and the moment, in one sentence.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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
            var shifts = await ledger.GetShiftsAsync();

            Shifts.SyncWith(
                shifts.Select(shift => new ShiftChoice(shift.Id,
                    $"{(shift.IsActive ? "Текущая" : "Завершённая")} · {shift.StartTime.ToLocalTime():dd.MM.yyyy HH:mm}" +
                    $"{(shift.EndTime.HasValue ? $" – {shift.EndTime.Value.ToLocalTime():HH:mm}" : string.Empty)}",
                    shift.EndTime.HasValue)),
                choice => choice.Id);

            /// <summary>THE DEFAULT IS THE SHIFT THAT IS OPEN RIGHT NOW — the owner's instruction, and it reverses the previous "prefer a closed shift" default.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            var next = Shifts.FirstOrDefault(choice => choice.Id == previousId)
                ?? Shifts.FirstOrDefault(choice => !choice.IsClosed)
                ?? Shifts.FirstOrDefault(choice => choice.IsClosed)
                ?? Shifts.FirstOrDefault();
            var switched = next?.Id != previousId;

            /// <summary>Folds are per-SHIFT state, not per-session state: a manager who folded away «Капучино» to read the rest of the day does not expect the fold to survive moving to...</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            if (switched) OpenAllDishes();

            /// <summary>Assigned with the reload suppressed: the analysis this method runs next IS the analysis for this selection, and letting the setter start one too would run the t...</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

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
            var stats = await reporting.GetShiftStatsAsync(SelectedShift.Id);
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

            /// <summary>ORDERING: the staleness sentence is built from all three of these, and each setter republishes it.</summary>
            /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

            Reconciliation = stats.Reconciliation;
            IsReconciliationStale = stats.IsReconciliationStale;

            OnPropertyChanged(nameof(AveragePreparationTime));
            OnPropertyChanged(nameof(AverageCompletionTime));

            ApplyAnalytics(await reporting.GetProductAnalyticsAsync(SelectedShift.Id));

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build the shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось построить аналитику");
        }
    }
}
