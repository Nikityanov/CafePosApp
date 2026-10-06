using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

/// <summary>
/// Whether a bundle may be sold at all, and what each of its slots becomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS LOGIC HAD NO TESTS BECAUSE IT WAS NOT REACHABLE.</b> <see cref="BundlePlan"/> was a private
/// nested record inside <c>MenuViewModel</c> with three private helpers beside it. Nothing outside the
/// class could ask it a question, so nothing did — and it stands between a cashier's tap and selling a
/// bundle the kitchen cannot make. It is in <c>Core</c> now and takes no UI, which is the only reason
/// this file exists.
/// </para>
/// <para>
/// The rule has two halves that pull in opposite directions, and each is asserted from both sides:
/// substitution says a sold-out slot is still sellable (via its substitute), blocking says a slot with
/// no substitute kills the WHOLE bundle. A change that made substitution refuse would pass a test that
/// only checked blocking; a change that made blocking substitute would pass a test that only checked
/// substitution.
/// </para>
/// </remarks>
public class BundlePlanTests
{
    private static readonly Guid CoffeeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CakeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WaterId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static Product Dish(Guid id, string name, long priceKopecks, bool available = true, bool deleted = false) =>
        new()
        {
            Id = id,
            Name = name,
            PriceKopecks = priceKopecks,
            IsAvailable = available,
            IsDeleted = deleted,
        };

    private static ComboComponent Slot(
        Guid productId,
        Product? product,
        int quantityPerUnit = 1,
        Product? substitute = null) =>
        new()
        {
            ProductId = productId,
            Product = product,
            QuantityPerUnit = quantityPerUnit,
            SubstituteProduct = substitute,
        };

    private static Combo Bundle(params ComboComponent[] slots)
    {
        var combo = new Combo { Id = Guid.NewGuid(), Name = "Комбо", PriceKopecks = 35000 };
        foreach (var slot in slots)
        {
            slot.ComboId = combo.Id;
            slot.Combo = combo;
        }

        combo.Components = [.. slots];
        return combo;
    }

    [Fact]
    public void A_bundle_of_available_dishes_can_be_sold_and_offers_one_option_per_slot()
    {
        var bundle = Bundle(
            Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000)),
            Slot(CakeId, Dish(CakeId, "Чизкейк", 28000)));

        var plan = BundlePlan.Describe(bundle);

        Assert.True(plan.CanBeSold);
        Assert.Null(plan.BlockedDishName);
        Assert.Equal(2, plan.Options.Count);
        Assert.Equal(2, plan.Selection.Count);
        Assert.Equal([CoffeeId, CakeId], plan.Options.Select(option => option.ProductId));
    }

    [Fact]
    public void The_reference_is_the_a_la_carte_sum_of_the_slots_and_is_not_the_price_charged()
    {
        var bundle = Bundle(
            Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000)),
            Slot(CakeId, Dish(CakeId, "Чизкейк", 28000)));

        var plan = BundlePlan.Describe(bundle);

        Assert.Equal(43000, plan.ReferenceKopecks);
        // 43 000 à la carte against a bundle that sells for 35 000: the plan quotes the saving and
        // says nothing about the price, because the price belongs to the template.
        Assert.Equal(35000, bundle.PriceKopecks);
    }

    [Fact]
    public void A_slot_taking_two_of_a_dish_is_counted_twice_in_the_reference()
    {
        // This is the difference between a discount being quoted and a bundle being sold short. The
        // plan carries no multiplier of its own; it comes from the slot and has to reach the sum.
        var bundle = Bundle(Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000), quantityPerUnit: 2));

        var plan = BundlePlan.Describe(bundle);

        Assert.Equal(30000, plan.ReferenceKopecks);
        Assert.Equal(2, Assert.Single(plan.Selection).QuantityPerUnit);
    }

    [Fact]
    public void A_slot_declaring_no_quantity_still_reads_as_one_rather_than_zero()
    {
        // A slot saved with 0 would otherwise make the reference understate the bundle by a whole dish
        // — and «по отдельности» would be wrong in the direction that overstates the discount.
        var bundle = Bundle(Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000), quantityPerUnit: 0));

        var plan = BundlePlan.Describe(bundle);

        Assert.Equal(15000, plan.ReferenceKopecks);
        Assert.Equal(1, Assert.Single(plan.Selection).QuantityPerUnit);
    }

    [Fact]
    public void A_sold_out_slot_is_still_sold_as_its_substitute_and_the_label_says_which_is_which()
    {
        // Substitution, not refusal. The label names BOTH dishes because a substitution is a decision
        // the operator never made: a sheet showing only the replacement's name sells something the
        // cashier never looked at.
        var cake = Dish(CakeId, "Чизкейк", 28000, available: false);
        var substitute = Dish(Guid.NewGuid(), "Тирамису", 32000);

        var bundle = Bundle(
            Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000)),
            Slot(CakeId, cake, substitute: substitute));

        var plan = BundlePlan.Describe(bundle);

        Assert.True(plan.CanBeSold);
        var replaced = plan.Options.Single(option => option.ProductId == CakeId);
        Assert.Equal("Тирамису (замена: Чизкейк)", replaced.Label);
        Assert.Equal(32000, replaced.UnitKopecks);
    }

    [Fact]
    public void A_substituted_slot_keeps_the_SLOTS_identifier_so_the_sale_is_not_refused()
    {
        // THE rule that is easy to break and breaks silently. ResolveOne matches an incoming component
        // to a catalogue slot by the slot's ProductId, so an option carrying the SUBSTITUTE's id is
        // refused at the till as "a dish that is no longer part of this bundle" — after the cashier has
        // already built the bundle.
        var substituteId = Guid.NewGuid();
        var bundle = Bundle(Slot(
            CakeId,
            Dish(CakeId, "Чизкейк", 28000, available: false),
            substitute: Dish(substituteId, "Тирамису", 32000)));

        var plan = BundlePlan.Describe(bundle);

        Assert.Equal(CakeId, Assert.Single(plan.Options).ProductId);
        Assert.DoesNotContain(substituteId, plan.Options.Select(option => option.ProductId));
    }

    [Fact]
    public void A_soft_deleted_dish_is_not_sellable_even_though_its_row_and_its_price_remain()
    {
        // It is off the menu while its row survives, so treating it as sellable keeps selling something
        // that can no longer be printed.
        var bundle = Bundle(Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000, available: true, deleted: true)));

        var plan = BundlePlan.Describe(bundle);

        Assert.False(plan.CanBeSold);
        Assert.Equal("Кофе", plan.BlockedDishName);
    }

    [Fact]
    public void A_slot_with_nothing_to_sell_blocks_the_WHOLE_bundle_and_offers_no_options()
    {
        // Not a partial sale. Offering two of three dishes and refusing at confirm is worse than
        // refusing before the sheet opens.
        var bundle = Bundle(
            Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000)),
            Slot(CakeId, Dish(CakeId, "Чизкейк", 28000, available: false)),
            Slot(WaterId, Dish(WaterId, "Вода", 8000)));

        var plan = BundlePlan.Describe(bundle);

        Assert.False(plan.CanBeSold);
        Assert.Equal("Чизкейк", plan.BlockedDishName);
        Assert.Empty(plan.Options);
        Assert.Empty(plan.Selection);
        Assert.Equal(0, plan.ReferenceKopecks);
    }

    [Fact]
    public void A_refusal_names_the_dish_by_identifier_when_its_row_is_gone()
    {
        // A hand-edited database can produce it even though a Restrict FK should make it impossible. An
        // identifier in a refusal is read by nobody, but it is traceable to a catalogue row — where
        // "a dish from the bundle" would not be.
        var bundle = Bundle(Slot(CoffeeId, product: null));

        var plan = BundlePlan.Describe(bundle);

        Assert.False(plan.CanBeSold);
        Assert.Equal(CoffeeId.ToString(), plan.BlockedDishName);
    }

    [Fact]
    public void The_slots_are_offered_in_catalogue_order_by_name_whatever_order_the_database_returned()
    {
        // ComboComponent carries no SortOrder and its query applies no ORDER BY, so the collection's own
        // order is whatever the join produced. Without this the tile's composition line and the sheet
        // could name the same bundle's first dish differently.
        var bundle = Bundle(
            Slot(CakeId, Dish(CakeId, "Чизкейк", 28000)),
            Slot(CoffeeId, Dish(CoffeeId, "Кофе", 15000)),
            Slot(WaterId, Dish(WaterId, "Вода", 8000)));

        var plan = BundlePlan.Describe(bundle);

        Assert.Equal(
            [WaterId, CoffeeId, CakeId],
            plan.Options.Select(option => option.ProductId));
        // Same order in the selection, or the sheet would open showing slots in a different sequence
        // than it offers them in.
        Assert.Equal(plan.Options.Select(option => option.ProductId), plan.Selection.Select(choice => choice.ProductId));
    }

    [Fact]
    public void An_empty_bundle_has_nothing_to_offer_and_blocks_nobody()
    {
        // A bundle with no slots is a catalogue mistake, not a stock-out. It must not report a
        // blocking dish, because there is no dish to name and nothing for the operator to go and fix.
        var plan = BundlePlan.Describe(Bundle());

        Assert.True(plan.CanBeSold);
        Assert.Null(plan.BlockedDishName);
        Assert.Empty(plan.Options);
        Assert.Equal(0, plan.ReferenceKopecks);
    }

    [Fact]
    public void A_null_bundle_is_rejected_rather_than_read_as_empty()
    {
        // The caller null-checks the tile and re-reads the template; a null reaching here means two
        // guards disagree, and quietly describing it as an empty bundle would sell a bundle of nothing.
        Assert.Throws<ArgumentNullException>(() => BundlePlan.Describe(null!));
    }
}