namespace CafePos.Core.Services;

/// <summary>
/// A cash count the operator has already given, together with the drawer figure it was counted
/// against.
/// </summary>
/// <remarks>
/// ONE TYPE FOR TWO NUMBERS, on purpose. A count cannot be judged still true without the figure it
/// was taken against, and a figure on its own is nothing the operator ever typed — so neither half
/// is representable here without the other, and a caller cannot store a pre-fill that forgets which
/// drawer it belongs to.
/// </remarks>
public readonly record struct PendingCashCount(long CountedKopecks, long ExpectedAgainstKopecks);

/// <summary>
/// What the end-of-shift count field should start with on a repeated attempt at closing a shift.
/// </summary>
/// <remarks>
/// In the core, free of Maui, of dialogs and of <c>ObservableCollection</c>, because this is a rule
/// about the drawer and not about a dialog — and because it is the rule that was only ever checked
/// by hand.
/// <para>
/// THE RULE: a previous count is offered back only while the drawer still holds what it held when it
/// was counted. Otherwise the live expectation is offered, which is what the prompt's own text
/// states out loud ("По учёту 5 580,00") and therefore cannot contradict.
/// </para>
/// <para>
/// The case this exists for was seen on a device: after a close was refused because orders were
/// still open, two refunds were taken while the operator went and closed those orders. The dialog
/// went on offering the earlier 6 660,00 in a field labelled «по учёту 5 580,00», and one tap on
/// «Закрыть смену» would have stored a 1 080 discrepancy that nobody ever counted — written to the
/// shift as fact, on the one figure the reconciliation exists to get right. Comparing the stored
/// <see cref="PendingCashCount.ExpectedAgainstKopecks"/> against the live figure closes that, and
/// returning the live figure is the only honest answer in the case where the two have parted company:
/// the operator's earlier count is still a true statement about money that is no longer in the till.
/// </para>
/// </remarks>
public static class CashCountPrefill
{
    /// <summary>
    /// The figure in kopecks to pre-fill: the previous count when it was counted against the drawer
    /// as it stands now, and <paramref name="expectedKopecks"/> in every other case — no previous
    /// count at all, or a drawer that moved since.
    /// </summary>
    /// <param name="previous">The count already collected in this close attempt, if there is one.</param>
    /// <param name="expectedKopecks">The drawer's figure right now, in kopecks.</param>
    public static long Resolve(PendingCashCount? previous, long expectedKopecks) =>
        previous is { } entry && entry.ExpectedAgainstKopecks == expectedKopecks
            ? entry.CountedKopecks
            : expectedKopecks;
}
