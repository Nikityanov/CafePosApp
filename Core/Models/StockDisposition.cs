namespace CafePos.Core.Models;

/// <summary>
/// What happens to the ingredients that were written off when an order is cancelled. The operator
/// picks this once per cancellation, because the two answers are genuinely different facts about
/// the bar, and neither of them can be inferred from the order:
/// <list type="bullet">
/// <item><see cref="LeaveWrittenOff"/> — the goods were made and handed over, or the shelf and the
/// terminal disagree about what is there. Nothing goes back: the ingredients were consumed.</item>
/// <item><see cref="ReturnToStock"/> — nothing was ever made (a mistake at the till, a customer who
/// left), so the write-off is undone and the ingredients come back.</item>
/// </list>
/// Stock is deliberately NOT restored automatically. On a discrepancy the count is already wrong,
/// and putting the shelf back the way the recipe said would restore a wrong number and hide the
/// discrepancy behind a clean audit trail.
/// </summary>
public enum StockDisposition
{
    LeaveWrittenOff,
    ReturnToStock
}
