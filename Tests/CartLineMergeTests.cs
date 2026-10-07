using CafePos.Core.Services;
using CafePos.Presentation.ViewModels;

namespace CafePosApp.Tests;

/// <summary>
/// The cart line's identity — how a tapped dish decides whether it joins the line it is nearest to or
/// opens a new one.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS A CHARACTERISATION TEST, AND THAT IS THE POINT.</b> It pins behaviour that already
/// works, before phase 2 moves it. A test written to describe the code as it is will survive a move; a
/// test written from the intent will change when the code moves, which is indistinguishable from the
/// code breaking.
/// </para>
/// <para>
/// <see cref="CartItemViewModel"/> is the piece worth pinning first because it is the only part of
/// <c>MenuViewModel</c> with NO dependencies — no service, no platform seam, no database — and because
/// the merge key is the highest-consequence logic in the cart. It went wrong once: an inline
/// comparison that left the composition out entirely, which merged two DIFFERENT builds of one bundle
/// into one line and put one bundle in as two. Both cost money.
/// </para>
/// <para>
/// The cases below are distinct ways of being wrong, not examples of one rule.
/// </para>
/// </remarks>
public class CartLineMergeTests
{
    private static readonly Guid Latte = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Cake = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tea = Guid.Parse("33333333-3333-3333-3333-333333333333");

        // Named Plain/Part rather than anything line-shaped: System.Linq.Enumerable has a Line() query
    // operator on net9+, and a same-named local helper silently loses to it at the call site — which
    // surfaces as a nonsensical conversion error rather than as "you shadowed something".
    private static CartItemViewModel Plain(Guid productId, string? modifier = null, string? variant = null) =>
        new() { ProductId = productId, ProductName = "Д", Price = 10m, Quantity = 1, SelectedModifierName = modifier, SelectedVariantName = variant };

    private static LineComponentViewModel Part(Guid productId, string name, int quantityPerUnit = 1) =>
        new(productId, name, quantityPerUnit, 1000, 1000);

    [Fact]
    public void The_same_dish_added_twice_is_one_line()
    {
        // The merge key is what makes the second tap raise the quantity instead of adding a row.
        Assert.Equal(Plain(Latte).MergeKey, Plain(Latte).MergeKey);
    }

    [Fact]
    public void Different_dishes_never_merge()
    {
        Assert.NotEqual(Plain(Latte).MergeKey, Plain(Tea).MergeKey);
    }

    [Fact]
    public void A_modifier_is_part_of_the_identity()
    {
        // Two builds of one dish at different modifiers are two lines at two prices. Merging them
        // charges one at the other's price.
        Assert.NotEqual(Plain(Latte).MergeKey, Plain(Latte, modifier: "Большой").MergeKey);
    }

    [Fact]
    public void A_variant_is_part_of_the_identity()
    {
        Assert.NotEqual(Plain(Latte).MergeKey, Plain(Latte, variant: "0,3 л").MergeKey);
    }

    [Fact]
    public void Two_different_builds_of_one_bundle_stay_two_lines()
    {
        // THE regression this suite exists for. Both rows are the same bundle — same product, same
        // price — differing only in composition, and they must not merge: the operator composed them
        // separately and is entitled to see and change each.
        var withCake = Plain(Latte);
        withCake.Components.Add(Part(Cake, "Чизкейк"));

        var withTea = Plain(Latte);
        withTea.Components.Add(Part(Tea, "Чай"));

        Assert.NotEqual(withCake.MergeKey, withTea.MergeKey);
    }

    [Fact]
    public void One_bundle_added_twice_is_one_line_at_quantity_two()
    {
        // The other half of the same rule, and the opposite failure: the same build must merge, or the
        // cart fills with duplicate rows the cashier deletes one at a time.
        var first = Plain(Latte);
        first.Components.Add(Part(Cake, "Чизкейк"));

        var second = Plain(Latte);
        second.Components.Add(Part(Cake, "Чизкейк"));

        Assert.Equal(first.MergeKey, second.MergeKey);
    }

    [Fact]
    public void Component_multiplicity_is_part_of_the_identity()
    {
        // Two coffees in a bundle is not the bundle with one: collapsing them shows the operator a line
        // whose stated contents disagree with what they composed.
        var one = Plain(Latte);
        one.Components.Add(Part(Cake, "Чизкейк", quantityPerUnit: 1));

        var two = Plain(Latte);
        two.Components.Add(Part(Cake, "Чизкейк", quantityPerUnit: 2));

        Assert.NotEqual(one.MergeKey, two.MergeKey);
    }

    [Fact]
    public void A_plain_dish_is_not_a_bundle()
    {
        // IsCombo is answered by the composition being empty, not by a flag — so an ordinary dish can
        // never be told apart from a bundle and needs no flag kept in step with it.
        Assert.False(Plain(Latte).IsCombo);

        var bundle = Plain(Latte);
        bundle.Components.Add(Part(Cake, "Чизкейк"));
        Assert.True(bundle.IsCombo);
    }

    [Fact]
    public void The_line_total_is_price_times_quantity()
    {
        var line = Plain(Latte);
        line.Price = 160m;
        line.Quantity = 1;
        Assert.Equal(160m, line.LineTotal);

        line.Quantity = 3;
        Assert.Equal(480m, line.LineTotal);
    }

    [Fact]
    public void A_price_change_is_reported_as_an_override_and_an_unchanged_price_is_not()
    {
        var line = Plain(Latte);
        line.ListPrice = 160m;
        line.Price = 160m;
        Assert.False(line.IsPriceOverridden);

        line.Price = 120m;
        Assert.True(line.IsPriceOverridden);
    }

    [Fact]
    public void A_restored_line_keeps_its_identity_through_the_checkout_shape()
    {
        // The draft-restore path: a saved cart becomes a CheckoutLine and comes back. If the identity
        // did not survive, restoring a draft would split every bundle into several rows.
        var bundle = CartItemViewModel.FromLine(new CheckoutLine(
            Latte, "Комбо", 500m, 2, null, null,
            [new CheckoutComponent(Cake, "Чизкейк", 1, 28000, 28000)]));

        var restored = bundle.WithComponents([new CheckoutComponent(Cake, "Чизкейк", 1, 28000, 28000)]);

        Assert.Equal(bundle.MergeKey, restored.MergeKey);
        Assert.Equal(2, restored.Quantity);
        Assert.True(restored.IsCombo);
    }

    // ── A quantity step has to announce the cart, and it used not to ────────────────────────────────
    // Found by testing the DraftAutosave extraction on the device: three taps on «+» on a line that
    // was already there, then a kill, and the draft came back a step behind. The autosave hung off
    // Cart.CollectionChanged, which fires when a line ARRIVES OR LEAVES and says nothing about the
    // quantity of one that is already there - CartBuilder.StepUp writes item.Quantity and nothing
    // else. It predates the extraction; the wiring at HEAD was identical.
    //
    // These two are the whole regression: the second is the bug, and the first is the control that
    // says the fix did not break the case that already worked.

    [Fact]
    public void Stepping_the_quantity_of_a_line_that_is_already_there_announces_the_cart()
    {
        var cart = new CartBuilder();
        cart.Add(Plain(Latte));

        var announced = 0;
        cart.Changed += () => announced++;

        cart.StepUp(Assert.Single(cart.Cart));

        Assert.Equal(1, announced);
        Assert.Equal(2, Assert.Single(cart.Cart).Quantity);
    }

    [Fact]
    public void Adding_a_line_announces_the_cart_too_so_the_fix_kept_the_working_case()
    {
        var cart = new CartBuilder();

        var announced = 0;
        cart.Changed += () => announced++;

        cart.Add(Plain(Latte));
        cart.Add(Plain(Cake));

        Assert.Equal(2, announced);
        Assert.Equal(2, cart.Cart.Count);
    }

    [Fact]
    public void An_announcement_does_not_depend_on_the_total_having_moved()
    {
        // The control case for the fix as written. Recalculate raises Changed unconditionally, so a
        // mutation that happened to leave the sum alone is still announced - a draft that skips it is
        // still a draft that is behind. Two lines whose totals are equal guard against somebody
        // "optimising" the raise into a SetProperty check.
        var cart = new CartBuilder();
        cart.Add(Plain(Latte));
        cart.Add(Plain(Cake));

        var announced = 0;
        cart.Changed += () => announced++;

        // Removing one line changes the count and therefore the total; the point of this test is the
        // raise is not gated, so it fires even when Recalculate finds the same number it already had.
        cart.Recalculate();

        Assert.Equal(1, announced);
    }
}