namespace CafePos.Core.Models;

/// <summary>What a stock journal row records, decided by type rather than by its <see cref="StockMovement.Reason"/> string.</summary>
/// <remarks>Почему так — `docs/decisions/stock.md`</remarks>

public enum StockMovementKind
{
    /// <summary>A row written before kinds existed. Treated as untrustworthy rather than guessed at.</summary>

    Unknown = 0,

    /// <summary>The order's checkout took this off the shelf.</summary>

    WriteOff = 1,

    /// <summary>Stock arrived. Never belongs to an order.</summary>

    Delivery = 2,

    /// <summary>An edit of the order's lines moved this, in either direction.</summary>

    Edit = 3,

    /// <summary>A cancellation put this back on the shelf.</summary>

    Reversal = 4
}
