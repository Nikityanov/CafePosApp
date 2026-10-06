using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// What an order records about money, and what the cashier is then told.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE POINT OF THIS SUITE IS THAT THE TWO ANSWERS CANNOT DISAGREE.</b> The checkout path chose them
/// with two independent ternaries over one flag: whether the order is written with a payment row, and
/// whether the operator is told «оплачено картой» or «оплата не получена». Nothing connected them. The
/// first test below is written to fail if either is changed alone — that is what makes it worth having,
/// and it is the reason the rule lives in one type rather than two call sites.
/// </para>
/// </remarks>
public class PaymentBookingTests
{
    [Theory]
    [InlineData(PaymentMethod.Cash, false, "оплачено наличными")]
    [InlineData(PaymentMethod.Card, false, "оплачено картой")]
    [InlineData(PaymentMethod.Cash, true, "оплата не получена")]
    [InlineData(PaymentMethod.Card, true, "оплата не получена")]
    public void A_paid_order_is_written_with_a_payment_and_says_so(PaymentMethod method, bool deferred, string expected)
    {
        var intent = PaymentBooking.IntentFor(method, 500m, deferred);
        var receipt = PaymentBooking.ReceiptText(method, deferred);

        Assert.Equal(expected, receipt);

        // The pairing, not either half. A payment row exists if and only if the receipt claims money.
        if (deferred)
        {
            Assert.Null(intent);
            Assert.Contains("не получена", receipt);
        }
        else
        {
            Assert.NotNull(intent);
            Assert.Contains("оплачено", receipt);
        }
    }

    [Fact]
    public void A_deferred_payment_writes_NO_payment_row_at_all()
    {
        // Not a zero, not a null amount, not a row marked deferred: no row. The two paths then write an
        // identical order and differ only in whether money exists behind it, which is the only thing a
        // report can honestly read.
        Assert.Null(PaymentBooking.IntentFor(PaymentMethod.Card, 500m, isDeferred: true));
    }

    [Fact]
    public void The_amount_that_was_taken_is_the_amount_that_is_recorded()
    {
        var intent = PaymentBooking.IntentFor(PaymentMethod.Cash, 1234.56m, isDeferred: false);

        Assert.NotNull(intent);
        Assert.Equal(1234.56m, intent.Amount);
        Assert.Equal(PaymentMethod.Cash, intent.Method);
    }

    /// <summary>
    /// One shortage, in the words the operator acts on.
    /// </summary>
    /// <remarks>
    /// <b>BOTH FIGURES, NOT JUST THE VERDICT.</b> «Недостаточно молока» refuses the sale and tells the
    /// operator nothing they can act on. «нужно 0,5 л, есть 0,2 л» answers whether to make the drink,
    /// substitute, or offer something else — which is the whole reason the message exists.
    /// <para>
    /// <b>THE SEPARATOR FOLLOWS THE DEVICE, AND THAT IS THE PROJECT'S CONVENTION.</b>
    /// <see cref="TextFormat.Quantity"/> uses <c>CurrentCulture</c>, so the same shortage reads 0,5 on a
    /// Russian phone and 0.5 on an English one — exactly as every other quantity in the app does, and
    /// money with it. I wrote this message with a hardcoded <c>{Required:0.##}</c> and got 0.5 in a
    /// test; the fix was not to pin a culture here but to stop having a second formatting rule.
    /// </para>
    /// </remarks>
    private static StockShortage Milk() =>
        new(Guid.NewGuid(), "Молоко", "л", Required: 0.5m, Available: 0.2m);

    [Fact]
    public void A_shortage_names_the_ingredient_and_BOTH_figures()
    {
        var shortage = Milk();

        var text = PaymentBooking.Describe(shortage);

        Assert.StartsWith("Молоко: нужно ", text, StringComparison.Ordinal);
        Assert.Contains(" есть ", text, StringComparison.Ordinal);
        // Both figures, not just the ingredient and the unit.
        // Split on the separator and require a figure on each side: one separator, two numbers.
        var halves = text.Split(" есть ", StringSplitOptions.None);
        Assert.Equal(2, halves.Length);
        Assert.All(halves, half => Assert.False(string.IsNullOrWhiteSpace(half)));
        Assert.Contains("л", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_figures_are_written_the_way_the_writes_every_other_quantity_in_the_app()
    {
        var expected = $"Молоко: нужно {TextFormat.Quantity(0.5m, "л")}, есть {TextFormat.Quantity(0.2m, "л")}";

        Assert.Equal(expected, PaymentBooking.Describe(Milk()));
    }

    [Fact]
    public void Whole_quantities_are_not_given_a_decimal_tail()
    {
        // «нужно 2,0 л» reads as a measurement that does not exist.
        var shortage = new StockShortage(Guid.NewGuid(), "Сахар", "кг", Required: 2m, Available: 1m);

        var text = PaymentBooking.Describe(shortage);

        Assert.DoesNotContain(".0 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(",0 ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quarter_is_not_rounded_away()
    {
        // Rounded to one decimal, 0,25 becomes 0,3 — and against 0,3 available it invents a shortage
        // that is not there.
        var shortage = new StockShortage(Guid.NewGuid(), "Корица", "г", Required: 0.25m, Available: 0.3m);

        Assert.Contains(TextFormat.Quantity(0.25m, null), PaymentBooking.Describe(shortage), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_unit_is_not_given_a_trailing_space()
    {
        var shortage = new StockShortage(Guid.NewGuid(), "Стаканы", "", Required: 10m, Available: 2m);

        var text = PaymentBooking.Describe(shortage);

        Assert.DoesNotContain("  ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Shortages_are_listed_in_the_order_they_arrive()
    {
        // The operator reads top to bottom; the order the planner returned is the order they were
        // produced in and the one a test can predict.
        var shortages = new[]
        {
            new StockShortage(Guid.NewGuid(), "Молоко", "л", 0.5m, 0.2m),
            new StockShortage(Guid.NewGuid(), "Сахар", "кг", 2m, 0m),
        };

        var described = PaymentBooking.DescribeAll(shortages);

        Assert.Equal(2, described.Count);
        Assert.StartsWith("Молоко", described[0], StringComparison.Ordinal);
        Assert.StartsWith("Сахар", described[1], StringComparison.Ordinal);
    }

    [Fact]
    public void No_shortages_is_an_empty_list_rather_than_a_something()
    {
        Assert.Empty(PaymentBooking.DescribeAll([]));
    }

    [Fact]
    public void A_method_with_no_instrumental_wording_falls_back_to_card_and_not_to_a_blank()
    {
        // Two methods today. A third added without a word here would silently print «оплачено » with
        // nothing after it, which reads as a truncated message rather than as a missing word.
        Assert.Equal("картой", PaymentText.Method((PaymentMethod)99));
    }
}
