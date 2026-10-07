namespace CafePos.Core.Common;

/// <summary>What stepping a cart line does, and what — if anything — the cashier can take back.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public sealed record CartStepDown(
    int QuantityAfter,
    bool RemovesLine,
    string? Announce,
    int? UndoQuantity)
{
    /// <summary>Wording that names what happened to the line.</summary>
    public const string RemovedMessage = " — убрано из корзины.";

    /// <summary>Steps a line's quantity down by one.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static CartStepDown From(int quantityBefore, string productName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantityBefore, 1);

        return quantityBefore == 1
            ? new CartStepDown(0, RemovesLine: true, $"{productName}{RemovedMessage}", UndoQuantity: quantityBefore)
            : new CartStepDown(quantityBefore - 1, RemovesLine: false, Announce: null, UndoQuantity: null);
    }

    /// <summary>Where a line goes back when a removal is undone, or -1 to mean «append». Where the line was when it was removed. How many lines the cart holds now.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static int RestoreIndex(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return index >= 0 && index <= count ? index : -1;
    }
}
