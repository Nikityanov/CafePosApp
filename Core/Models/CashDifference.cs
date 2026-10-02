namespace CafePos.Core.Models;

/// <summary>
/// What a physical count of the drawer turned out to be against what the ledger said it held.
/// Three named states and no amounts: the amounts belong to
/// <see cref="CafePos.Core.Services.CashReconciliation"/>, which derives them from the counted and
/// the expected figures. A named difference is a READING of those two numbers for a screen and a
/// log line — a fourth copy of the same arithmetic would be a fourth thing to keep in step.
/// </summary>
public enum CashDifference
{
    /// <summary>The drawer holds exactly what the ledger expected. Needs no reason.</summary>
    Matched,

    /// <summary>
    /// Less cash than expected. This is the case the feature exists for: an empty drawer counted as
    /// 0 against a non-zero expectation is a shortage, and it has to be explained.
    /// </summary>
    Shortage,

    /// <summary>
    /// More cash than expected — change paid out of the wrong till, a payment taken but not
    /// recorded, or a float that was not opened through the app.
    /// </summary>
    Overage
}