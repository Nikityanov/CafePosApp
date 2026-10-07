using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Решение о списании при правке заказа, без базы. Интеграционные тесты в
/// <c>OrderEditStockTests</c> доказывают, что сервис это решение применяет; здесь — что оно
/// верно на границах, которые интеграцией не достать: ровно нужное количество, две ошибки на
/// разных ингредиентах сразу, и то, какой положительный ряд в журнале мешает возврату, а какой нет.
/// </summary>
public class StockDeltaPlanTests
{
    private static readonly Guid Milk = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Beans = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static StockPosition Position(
        Guid? ingredientId = null,
        decimal available = 1000m,
        decimal consumed = 200m,
        decimal requiredBefore = 200m,
        decimal requiredAfter = 200m) =>
        new(
            ingredientId ?? Milk,
            ingredientId == Beans ? "Зерно" : "Молоко",
            ingredientId == Beans ? "г" : "мл",
            available,
            consumed,
            requiredBefore,
            requiredAfter);

    [Fact]
    public void An_edit_that_changes_no_dish_produces_no_movement()
    {
        var plan = StockDeltaPlan.Decide([Position()]);

        Assert.True(plan.IsEmpty);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void A_reduced_dish_goes_back_onto_the_shelf()
    {
        // Списано 400 за две чашки, осталось требование одной: 200 на полку.
        var plan = StockDeltaPlan.Decide([Position(consumed: 400m, requiredBefore: 400m, requiredAfter: 200m)]);

        Assert.Equal(200m, Assert.Single(plan.Deltas).QuantityDelta);
    }

    [Fact]
    public void An_added_dish_comes_off_the_shelf()
    {
        var plan = StockDeltaPlan.Decide([Position(consumed: 200m, requiredBefore: 200m, requiredAfter: 600m)]);

        Assert.Equal(-400m, Assert.Single(plan.Deltas).QuantityDelta);
    }

    [Fact]
    public void Exactly_enough_stock_is_enough()
    {
        // Граница: нужно ровно то, что есть. Отказ здесь был бы ошибкой — половину того, что есть,
        // списать можно всегда.
        var plan = StockDeltaPlan.Decide([Position(available: 400m, consumed: 200m, requiredBefore: 200m, requiredAfter: 600m)]);

        Assert.Empty(plan.Shortages);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void A_hundredth_short_is_a_shortage()
    {
        var plan = StockDeltaPlan.Decide([Position(available: 399.99m, consumed: 200m, requiredBefore: 200m, requiredAfter: 600m)]);

        var shortage = Assert.Single(plan.Shortages);
        Assert.Equal(400m, shortage.Required);
        Assert.Equal(399.99m, shortage.Available);
        Assert.Empty(plan.Deltas);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void The_shortage_is_reported_against_what_is_still_needed_not_the_whole_dish()
    {
        // Заказ уже съел 200 и вернуть их нельзя — они на полке не лежат. «Нужно 600, есть 0»
        // ввело бы в заблуждение: 600 — это весь заказ, а недостаёт ровно 400.
        var plan = StockDeltaPlan.Decide([Position(available: 0m, consumed: 200m, requiredBefore: 200m, requiredAfter: 600m)]);

        Assert.Equal(400m, Assert.Single(plan.Shortages).Required);
    }

    [Fact]
    public void A_recipe_that_moved_after_checkout_is_refused_rather_than_guessed_at()
    {
        // Журнал говорит «снято 200», рецепт сегодня требует 500. Дельта означала бы возврат 300
        // молока, которого с полки не снимали.
        var plan = StockDeltaPlan.Decide([Position(consumed: 200m, requiredBefore: 500m, requiredAfter: 500m)]);

        var drift = Assert.Single(plan.RecipeDrift);
        Assert.Equal(200m, drift.Consumed);
        Assert.Equal(500m, drift.ByRecipe);
        Assert.Empty(plan.Deltas);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Two_ingredients_failing_two_different_ways_are_both_reported()
    {
        // Молоко — рецепт уехал, зерна — не хватает. Ошибка одного не должна прятать вторую, и
        // отказ всё равно один: полка не тронута ни тем, ни другим.
        var plan = StockDeltaPlan.Decide(
        [
            Position(Milk, consumed: 200m, requiredBefore: 500m, requiredAfter: 500m),
            Position(Beans, available: 10m, consumed: 20m, requiredBefore: 20m, requiredAfter: 40m)
        ]);

        Assert.Single(plan.RecipeDrift);
        Assert.Single(plan.Shortages);
        Assert.Empty(plan.Deltas);
        Assert.False(plan.CanApply);
    }

    [Fact]
    public void Each_ingredient_is_decided_on_its_own_numbers()
    {
        var plan = StockDeltaPlan.Decide(
        [
            Position(Milk, consumed: 400m, requiredBefore: 400m, requiredAfter: 200m),
            Position(Beans, available: 500m, consumed: 20m, requiredBefore: 20m, requiredAfter: 100m),
            Position(Guid.Parse("33333333-3333-3333-3333-333333333333"), consumed: 50m, requiredBefore: 50m, requiredAfter: 50m)
        ]);

        Assert.Equal(2, plan.Deltas.Count);
        Assert.Equal(200m, plan.Deltas[0].QuantityDelta);
        Assert.Equal(-80m, plan.Deltas[1].QuantityDelta);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void Nothing_to_decide_is_a_decision_to_do_nothing()
    {
        var plan = StockDeltaPlan.Decide([]);

        Assert.True(plan.IsEmpty);
        Assert.True(plan.CanApply);
        Assert.Empty(plan.DescribeShortages());
    }

    /// <summary>
    /// Предохранитель журнала. Узкое место правки: положительный ряд в журнале заказа — это
    /// приход, и прибавлять к нему минус нельзя, потому что прихода не было.
    /// </summary>
    [Fact]
    public void An_empty_or_write_off_only_journal_describes_the_order()
    {
        StockPlan.EnsureJournalDescribesTheOrder([], 1);
        StockPlan.EnsureJournalDescribesTheOrder(
        [
            new StockMovement { IngredientId = Milk, QuantityDelta = -200m, Kind = StockMovementKind.WriteOff },
            // Ряд самой правки может быть положительным — убранное блюдо возвращает ингредиент.
            // Прежний запрет на ЛЮБОЙ положительный ряд запрещал бы и это, то есть после починки
            // отменить правленый заказ с возвратом было бы нельзя.
            new StockMovement { IngredientId = Milk, QuantityDelta = 200m, Kind = StockMovementKind.Edit },
            // Ряд из базы, где видов ещё не было. Отрицательный знак говорит «расход» независимо от
            // того, как он назывался, поэтому в сумму он идёт, а не блокирует.
            new StockMovement { IngredientId = Milk, QuantityDelta = -50m, Kind = StockMovementKind.Unknown }
        ], 1);
    }

    [Fact]
    public void A_positive_row_from_before_kinds_existed_stops_the_reversal_too()
    {
        // Неизвестный и положительный — единственное сочетание, которое здесь опасно: что это
        // такое, по строке не прочитать, а прибавлять минус к приходу нельзя, потому что прихода
        // не было.
        var exception = Assert.Throws<ConflictException>(() => StockPlan.EnsureJournalDescribesTheOrder(
            [new StockMovement { IngredientId = Milk, QuantityDelta = 200m, Kind = StockMovementKind.Unknown }], 12));

        Assert.Contains("#12", exception.Message);
    }

    [Fact]
    public void A_delivery_booked_against_the_order_stops_the_reversal()
    {
        var exception = Assert.Throws<ConflictException>(() => StockPlan.EnsureJournalDescribesTheOrder(
            [new StockMovement { IngredientId = Milk, QuantityDelta = 500m, Kind = StockMovementKind.Delivery, Reason = "Поставка по заказу" }], 12));

        Assert.Contains("#12", exception.Message);
    }
}
