using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>The shapes returns.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public sealed record ShiftStats(
    int AllOrdersCount,
    int CompletedCount,
    int CancelledCount,
    int OpenCount,
    decimal Revenue,
    decimal AverageCheck,
    int ItemsCount,
    double AveragePreparationMinutes,
    double AverageCompletionMinutes,
    string PeakHour,
    // "Принято оплат": what the till actually recorded, split by method.
    // Почему так — `docs/decisions/orders.md`

    int PaymentsCount,
    decimal PaymentsCash,
    decimal PaymentsCard,
    decimal PaymentsTotal,
    // "Возвращено": money that left the till again, split by the method it left in.
    // Почему так — `docs/decisions/orders.md`

    decimal RefundsCash,
    decimal RefundsCard,
    decimal RefundsTotal,
    // "Внесено размена" and "Изъято на инкассацию": money that moved through the drawer by hand, which is to say without a customer behind it.
    // Почему так — `docs/decisions/orders.md`

    decimal FloatCash,
    decimal PayoutCash,
    // The cash reconciliation, appended strictly last and additively like the Payments* block above.
    // Почему так — `docs/decisions/orders.md`

    decimal ExpectedCashNow,
    CashReconciliation? Reconciliation,
    bool IsReconciliationStale);

/// <summary>The recorded cash count of one closed shift: what the ledger said the drawer held at the close (<paramref name="ExpectedKopecks"/>, frozen and never m…</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public sealed record CashReconciliation(
    long ExpectedKopecks,
    long CountedKopecks,
    DateTimeOffset CountedAt,
    string? Reason)
{
    /// <summary>Counted − Expected. Negative is a shortage, positive an overage.</summary>
    public long DiscrepancyKopecks => CountedKopecks - ExpectedKopecks;

    public CashDifference Difference => DiscrepancyKopecks switch
    {
        0 => CashDifference.Matched,
        < 0 => CashDifference.Shortage,
        _ => CashDifference.Overage
    };
}

/// <summary>One line of the shift's product breakdown.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public sealed record ProductAnalyticsRowData(
    string ProductName,
    string ModifierName,
    int Quantity,
    decimal Revenue,
    Guid? CategoryId,
    string? CategoryName);

/// <summary>One order line whose charged price differs from the price it was allowed to be sold at, with the size of the difference.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public sealed record DiscountedLine(
    int OrderNumber,
    OrderStatus Status,
    string ProductName,
    string? ModifierName,
    string? VariantName,
    int Quantity,
    long ListPriceKopecks,
    long PriceKopecks,
    long? ReferenceTotalKopecks)
{
    /// <summary>What the order lost on this line: (allowed − charged) × quantity. Negative if a price was RAISED.</summary>
    public long DiscountKopecks => (ListPriceKopecks - PriceKopecks) * Quantity;

    /// <summary>What was charged for the whole line, for comparison with <see cref="ReferenceTotalKopecks"/>.</summary>
    public long ChargedTotalKopecks => PriceKopecks * Quantity;

    /// <summary>Whether the line is a bundle, i.e. whether it has a composition at all.</summary>
    public bool IsBundle => ReferenceTotalKopecks.HasValue;

    /// <summary>How much less the bundle cost than its parts, for the whole line.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public long? BundleSavingKopecks => ReferenceTotalKopecks - ChargedTotalKopecks;
}
