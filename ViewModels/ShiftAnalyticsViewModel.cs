using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CafePosApp.ViewModels;

/// <summary>One row of the shift picker.</summary>
/// <param name="Id">The shift to analyse.</param>
/// <param name="DisplayName">«Текущая» / «Завершённая» plus the local times.</param>
/// <param name="IsClosed">
/// Whether the shift has an end time. Carried as data rather than re-derived from the name because
/// the picker's default selection IS this flag: it is what makes «Анализировать смену» land on the
/// shift that was just closed instead of on the empty one the close just opened.
/// </param>
public sealed record ShiftChoice(Guid Id, string DisplayName, bool IsClosed);

public sealed record ProductAnalyticsRow(string ProductName, string ModifierName, int Quantity, decimal Revenue);

public class ShiftAnalyticsViewModel : ObservableObject
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
    }

    public ObservableCollection<ShiftChoice> Shifts { get; } = [];
    public ObservableCollection<ProductAnalyticsRow> ProductRows { get; } = [];

    private ShiftChoice? selectedShift;
    public ShiftChoice? SelectedShift { get => selectedShift; set => SetProperty(ref selectedShift, value); }

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

    private decimal cardTotal;
    public decimal CardTotal
    {
        get => cardTotal;
        private set
        {
            if (SetProperty(ref cardTotal, value)) NotifyCardComposition();
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
    /// card taller than the eight around it for no information, and this page has no ScrollView —
    /// every dp here comes out of the list below.
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

            // PREFER A CLOSED SHIFT over Shifts.FirstOrDefault(). GetShiftsAsync puts the active
            // shift first, so the old default opened this page on the shift that just started: the
            // operator who closed a shift, tapped «Анализировать смену» to check that the count was
            // recorded, and was shown an empty hour instead. EndTime is the closed test rather than
            // !IsActive, because the domain's own definition of "this shift ended" is the moment it
            // ended, not the flag that happened to be flipped at the time. GetShiftsAsync orders
            // closed shifts newest first, so the first one is the shift just closed.
            SelectedShift = Shifts.FirstOrDefault(choice => choice.Id == previousId)
                ?? Shifts.FirstOrDefault(choice => choice.IsClosed)
                ?? Shifts.FirstOrDefault();
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
            ItemsCount = stats.ItemsCount;
            averagePreparationMinutes = stats.AveragePreparationMinutes;
            averageCompletionMinutes = stats.AverageCompletionMinutes;
            PeakHour = stats.PeakHour;

            // The same one definition the shift report and the CSV export read. Never PaymentsCash −
            // RefundsCash written here: a second definition of the drawer is how two reports end up
            // disagreeing about whether the till balanced.
            CashInDrawer = stats.ExpectedCashNow;
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

            var rows = await orders.GetProductAnalyticsAsync(SelectedShift.Id);
            ProductRows.SyncWith(
                rows.Select(row => new ProductAnalyticsRow(row.ProductName, row.ModifierName, row.Quantity, row.Revenue)),
                row => $"{row.ProductName}|{row.ModifierName}");

            Message = string.Empty;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build the shift analytics");
            Message = UserMessages.Describe(exception, "Не удалось построить аналитику");
        }
    }
}
