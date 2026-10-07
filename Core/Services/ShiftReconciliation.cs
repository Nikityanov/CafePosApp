using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>The one place that turns the four stored reconciliation columns of a `Shift` into a `CashReconciliation`.</summary>
/// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

internal static class ShiftReconciliation
{
    /// <summary>The recorded count of <paramref name="shift"/>, or null when it was never counted — which covers every shift that predates the feature.</summary>
    /// <remarks>Почему так — `docs/decisions/cash.md`</remarks>

    public static CashReconciliation? Read(Shift? shift)
    {
        if (shift?.CountedCashKopecks is not long counted || shift.ExpectedCashKopecks is not long expected)
            return null;

        return new CashReconciliation(
            expected,
            counted,
            // "Recorded at", never "counted at": ReconciledAt is the moment the close was saved, which is not the moment the drawer was physically counted — the col…
            // Почему так — `docs/decisions/cash.md`

            shift.ReconciledAt ?? shift.EndTime ?? shift.StartTime,
            string.IsNullOrWhiteSpace(shift.CashDiscrepancyReason) ? null : shift.CashDiscrepancyReason);
    }

    /// <summary>Whether the frozen expectation and the live drawer figure have parted company. Both numbers are needed, so the caller has to already have both — this is the arithmetic, not the query.</summary>

    public static bool IsStale(CashReconciliation? reconciliation, long cashInDrawerKopecks) =>
        reconciliation is not null && reconciliation.ExpectedKopecks != cashInDrawerKopecks;
}
