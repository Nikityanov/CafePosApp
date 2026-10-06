namespace CafePos.Core.Common;

/// <summary>
/// What stepping a cart line does, and what — if anything — the cashier can take back.
/// </summary>
/// <remarks>
/// <para>
/// This is the one part of the cart that is a <i>decision</i> rather than a mutation, and it was
/// written inline inside <c>MenuViewModel</c> where nothing could ask it a question. The mutations
/// stay on the ViewModel — they touch an <c>ObservableCollection</c> bound in XAML — but the rule
/// below is pure and is now testable.
/// </para>
/// <para>
/// <b>ONLY THE STEP THAT REMOVES THE LINE ARMS AN UNDO.</b> Stepping 3 down to 2 is not a mistake worth
/// a control: the cashier can step back up. A line leaving the cart is the one edit on this screen with
/// no visible way back, and NN/g's finding that users «accidentally added the same item to their cart
/// multiple times» is that same failure seen from the other side. So an unintended edit is corrected
/// by an affordance, not by another tap on the line that is no longer there.
/// </para>
/// </remarks>
/// <param name="QuantityAfter">What the line's own quantity becomes.</param>
/// <param name="RemovesLine">True when the line leaves the cart entirely and should be taken out of it.</param>
/// <param name="Announce">
/// What the message strip says, or <c>null</c> for silence. Silence means the ViewModel must not touch
/// the message property at all: assigning an empty string would retire a pending undo, because that is
/// what that property's setter does.
/// </param>
/// <param name="UndoQuantity">
/// The quantity to restore the line to if the removal is undone, or <c>null</c> when no undo is armed.
/// </param>
public sealed record CartStepDown(
    int QuantityAfter,
    bool RemovesLine,
    string? Announce,
    int? UndoQuantity)
{
    /// <summary>Wording that names what happened to the line.</summary>
    public const string RemovedMessage = " — убрано из корзины.";

    /// <summary>
    /// Steps a line's quantity down by one.
    /// </summary>
    /// <param name="quantityBefore">
    /// The quantity as it stands NOW, before the decrement. Passed in rather than read from the line so
    /// the undo can restore the line as it was: the ViewModel decrements the line before removing it,
    /// so an undo armed from the value afterwards would bring back a row reading «0».
    /// </param>
    /// <param name="productName">The line's name, for the message.</param>
    public static CartStepDown From(int quantityBefore, string productName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantityBefore, 1);

        return quantityBefore == 1
            ? new CartStepDown(0, RemovesLine: true, $"{productName}{RemovedMessage}", UndoQuantity: quantityBefore)
            : new CartStepDown(quantityBefore - 1, RemovesLine: false, Announce: null, UndoQuantity: null);
    }

    /// <summary>
    /// Where a line goes back when a removal is undone, or <c>-1</c> to mean «append».
    /// </summary>
    /// <remarks>
    /// <b>A CART READ TOP TO BOTTOM IS A SEQUENCE.</b> A cashier who removes the third line and undoes
    /// it expects the third line back, not the last one — appending silently reorders a cart they have
    /// already read.
    /// <para>
    /// The index can be out of range if the cart changed underneath in the meantime: a draft restored,
    /// a line added and removed again. Appending is the honest fallback for that, and it is far better
    /// than throwing from a tap on «Отменить».
    /// </para>
    /// </remarks>
    /// <param name="index">Where the line was when it was removed.</param>
    /// <param name="count">How many lines the cart holds now.</param>
    public static int RestoreIndex(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return index >= 0 && index <= count ? index : -1;
    }
}