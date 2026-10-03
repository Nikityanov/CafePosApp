using CafePos.Core.Services;

namespace CafePosApp.Tests;

public class CashCountPrefillTests
{
    // The figures from the device this rule was written for: a close refused with orders still open,
    // two refunds taken while the operator went and closed them, so the drawer fell from 6 660,00 to
    // 5 580,00 and the dialog went on offering the earlier count.
    private const long CountedAgainstSixThousand = 666000;
    private const long DrawerAfterRefunds = 558000;

    [Fact]
    public void Resolve_offers_the_live_expectation_when_nothing_has_been_counted_yet()
    {
        var prefill = CashCountPrefill.Resolve(previous: null, expectedKopecks: DrawerAfterRefunds);

        Assert.Equal(DrawerAfterRefunds, prefill);
    }

    [Fact]
    public void Resolve_offers_the_previous_count_while_the_drawer_has_not_moved()
    {
        var previous = new PendingCashCount(CountedKopecks: CountedAgainstSixThousand, ExpectedAgainstKopecks: DrawerAfterRefunds);

        var prefill = CashCountPrefill.Resolve(previous, DrawerAfterRefunds);

        Assert.Equal(CountedAgainstSixThousand, prefill);
    }

    /// <summary>
    /// The bug this exists for: the count is still a true memory of what the operator counted, but it
    /// was counted against a drawer that has since lost 1 080,00 to refunds. Offering it would put a
    /// discrepancy into a prompt whose own text says «По учёту 5 580,00», and one tap would write it to
    /// the shift.
    /// </summary>
    [Fact]
    public void Resolve_offers_the_live_expectation_when_the_drawer_moved_after_a_refused_close()
    {
        var previous = new PendingCashCount(CountedKopecks: CountedAgainstSixThousand, ExpectedAgainstKopecks: CountedAgainstSixThousand);

        var prefill = CashCountPrefill.Resolve(previous, DrawerAfterRefunds);

        Assert.Equal(DrawerAfterRefunds, prefill);
    }

    /// <summary>
    /// A count of 0 is a real count — an emptied drawer — and is not the same as no count at all. A
    /// "is there anything to reuse" test written as a truthiness check on the number would quietly
    /// turn this case back into the live figure, which is not what the operator counted.
    /// </summary>
    [Fact]
    public void Resolve_offers_a_counted_zero_as_a_count()
    {
        var previous = new PendingCashCount(CountedKopecks: 0, ExpectedAgainstKopecks: DrawerAfterRefunds);

        var prefill = CashCountPrefill.Resolve(previous, DrawerAfterRefunds);

        Assert.Equal(0, prefill);
    }
}