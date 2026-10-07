namespace CafePos.Core.Models;

/// <summary>What happens to the ingredients that were written off when an order is cancelled.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public enum StockDisposition
{
    LeaveWrittenOff,
    ReturnToStock
}
