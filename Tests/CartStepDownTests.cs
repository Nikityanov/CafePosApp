using CafePos.Core.Common;

namespace CafePosApp.Tests;

/// <summary>
/// Stepping a cart line down: what changes, and what the cashier can take back.
/// </summary>
/// <remarks>
/// <para>
/// This rule was inline inside <c>MenuViewModel</c>, where nothing could ask it a question, and it has
/// already cost a real defect once: the emulator showed a cart restored to a row reading «0» with
/// «Итого 0,00», and the domain then refused the order at checkout with «У каждой позиции заказа должно
/// быть положительное количество».
/// </para>
/// <para>
/// The quantity is therefore read BEFORE the step down and travels with the undo, because the
/// ViewModel decrements the line before removing it. That ordering is the whole reason this is a
/// separate decision rather than three lines of arithmetic.
/// </para>
/// </remarks>
public class CartStepDownTests
{
    [Theory]
    [InlineData(5, 4)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void Stepping_down_from_more_than_one_leaves_the_line_in_place(int before, int after)
    {
        var step = CartStepDown.From(before, "Латте");

        Assert.Equal(after, step.QuantityAfter);
        Assert.False(step.RemovesLine);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    public void Stepping_down_without_removing_the_line_arms_no_undo_and_says_nothing(int before)
    {
        // Stepping 3 down to 2 is not a mistake worth a control — the cashier can step back up. An
        // undo here would also retire whatever undo the strip was already offering, which is a worse
        // outcome than no control at all.
        var step = CartStepDown.From(before, "Латте");

        Assert.Null(step.UndoQuantity);
        Assert.Null(step.Announce);
    }

    [Fact]
    public void Stepping_the_last_one_away_removes_the_line_and_names_it()
    {
        var step = CartStepDown.From(1, "Латте");

        Assert.Equal(0, step.QuantityAfter);
        Assert.True(step.RemovesLine);
        Assert.Equal("Латте — убрано из корзины.", step.Announce);
    }

    [Fact]
    public void The_undo_restores_the_quantity_the_line_had_BEFORE_the_step()
    {
        // THE REGRESSION. The ViewModel decrements before removing, so a quantity read afterwards is
        // zero. Restoring that puts a row on the cart reading «0», the total reads «Итого 0,00», and
        // checkout is refused in the domain.
        var step = CartStepDown.From(1, "Латте");

        Assert.Equal(1, step.UndoQuantity);
        Assert.NotEqual(0, step.UndoQuantity);
    }

    [Fact]
    public void Only_removing_a_line_arms_an_undo()
    {
        // A cart the cashier miscounted down by one is recoverable by stepping up. A line that has left
        // is not, which is why exactly one of these offers the control.
        var removes = CartStepDown.From(1, "Латте");
        var steps = CartStepDown.From(2, "Латте");

        Assert.True(removes.UndoQuantity is not null);
        Assert.Null(steps.UndoQuantity);
    }

    [Fact]
    public void A_line_with_no_quantity_is_refused_rather_than_stepped_to_minus_one()
    {
        // A line reading 0 in the cart is the state the bug above produced. Stepping it "down" from
        // there would remove it silently and arm an undo for zero, which is the same defect wearing a
        // different hat.
        Assert.Throws<ArgumentOutOfRangeException>(() => CartStepDown.From(0, "Латте"));
    }

    [Fact]
    public void A_removed_line_goes_back_to_where_it_was()
    {
        // A cart read top to bottom is a sequence. Removing the third line and undoing must give the
        // third line back, not the last one.
        Assert.Equal(2, CartStepDown.RestoreIndex(2, count: 4));
    }

    [Fact]
    public void A_line_removed_from_the_end_goes_back_to_the_end()
    {
        Assert.Equal(4, CartStepDown.RestoreIndex(4, count: 4));
    }

    [Fact]
    public void An_index_past_the_end_appends_rather_than_throwing()
    {
        // The cart can change underneath the undo: a draft restored, a line added and removed again.
        // Throwing from a tap on «Отменить» would lose the line the cashier was trying to recover.
        Assert.Equal(-1, CartStepDown.RestoreIndex(5, count: 4));
        Assert.Equal(-1, CartStepDown.RestoreIndex(99, count: 0));
    }

    [Fact]
    public void A_negative_index_appends_rather_than_throwing()
    {
        Assert.Equal(-1, CartStepDown.RestoreIndex(-1, count: 3));
    }

    [Fact]
    public void An_undone_line_can_land_at_either_end_of_an_emptied_cart()
    {
        Assert.Equal(0, CartStepDown.RestoreIndex(0, count: 0));
    }

    [Fact]
    public void A_negative_cart_size_is_refused_rather_than_silently_appending()
    {
        // A caller with a broken count would otherwise get «append» for every index, which looks like
        // a working undo and quietly reorders the cart.
        Assert.Throws<ArgumentOutOfRangeException>(() => CartStepDown.RestoreIndex(0, count: -1));
    }
}