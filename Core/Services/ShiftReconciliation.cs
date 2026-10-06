using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// The one place that turns the four stored reconciliation columns of a <see cref="Shift"/> into a
/// <see cref="CashReconciliation"/>. Shared by the shift report and the CSV export so that both say
/// the same thing about "was this shift counted, and against what".
/// <para>
/// The mapping is deliberately trivial, because the interesting decisions were made when the
/// columns were written: both money figures are present together or not at all, and they are frozen
/// rather than recomputed.
/// </para>
/// </summary>
internal static class ShiftReconciliation
{
    /// <summary>
    /// The recorded count of <paramref name="shift"/>, or null when it was never counted — which
    /// covers every shift that predates the feature. NOT null and NOT zero for a counted empty
    /// drawer: a count of 0 against a non-zero expectation is missing money, and it is the one case
    /// the whole feature exists to catch.
    /// </summary>
    public static CashReconciliation? Read(Shift? shift)
    {
        if (shift?.CountedCashKopecks is not long counted || shift.ExpectedCashKopecks is not long expected)
            return null;

        return new CashReconciliation(
            expected,
            counted,
            // "Recorded at", never "counted at": ReconciledAt is the moment the close was saved, which
            // is not the moment the drawer was physically counted — the column says when the fact
            // became part of the record. It equals EndTime to the second, and nothing derives from
            // it. The fallbacks only fire on a hand-edited row: a count is still reported rather
            // than dropped, because dropping it would hide the only evidence the drawer was counted.
            shift.ReconciledAt ?? shift.EndTime ?? shift.StartTime,
            string.IsNullOrWhiteSpace(shift.CashDiscrepancyReason) ? null : shift.CashDiscrepancyReason);
    }

    /// <summary>
    /// Whether the frozen expectation and the live drawer figure have parted company. Both numbers
    /// are needed, so the caller has to already have both — this is the arithmetic, not the query.
    /// </summary>
    public static bool IsStale(CashReconciliation? reconciliation, long cashInDrawerKopecks) =>
        reconciliation is not null && reconciliation.ExpectedKopecks != cashInDrawerKopecks;
}
