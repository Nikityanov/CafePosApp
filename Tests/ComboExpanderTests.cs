using CafePos.Core.Common;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Разворот комбо в список (блюдо, сколько) для существующего планировщика списания. Проверяется без
/// базы: разворот — это чистая функция, и именно поэтому его можно проверить целиком, тогда как
/// списание ингредиентов проверяется интеграционными тестами кассы.
/// </summary>
public class ComboExpanderTests
{
    private static readonly Guid Espresso = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Croissant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Juice = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static CheckoutComponent Component(Guid productId, string name, int quantityPerUnit = 1, long unitKopecks = 10000) =>
        new(productId, name, quantityPerUnit, unitKopecks, unitKopecks);

    private static CheckoutLine Line(Guid productId, string name, int quantity = 1) =>
        new(productId, name, 20000m, quantity);

    private static CheckoutLine Bundle(Guid productId, string name, int quantity, params CheckoutComponent[] components) =>
        new(productId, name, 35000m, quantity, null, null, components);

    [Fact]
    public void A_line_without_components_demands_itself()
    {
        // Ветка, по которой проходит каждый существующий чек: у обычного блюда нет состава, и оно
        // списывается само. Если бы это изменилось, списание перестало бы работать у всех сразу.
        var demands = ComboExpander.Expand([Line(Espresso, "Эспрессо", quantity: 2)]);

        Assert.Single(demands);
        Assert.Equal(Espresso, demands[0].ProductId);
        Assert.Equal(2, demands[0].Quantity);
        Assert.Equal("Эспрессо", demands[0].Name);
    }

    [Fact]
    public void A_bundle_demands_its_slots_instead_of_itself()
    {
        // Сам комбо в список не попадает: у него нет ни рецепта, ни своего склада. Считать и его
        // значило бы удвоить списание, если бы кто-нибудь завёл рецепт и на комбо.
        var demands = ComboExpander.Expand(
        [
            Bundle(Croissant, "Латте с круассаном", 1,
                Component(Espresso, "Эспрессо"),
                Component(Juice, "Сок"))
        ]);

        Assert.Equal(2, demands.Count);
        Assert.DoesNotContain(demands, demand => demand.ProductId == Croissant);
        Assert.Equal([Espresso, Juice], demands.Select(demand => demand.ProductId));
    }

    [Fact]
    public void A_quantity_on_a_bundle_means_that_many_complete_bundles()
    {
        // N на строке комбо — это N одинаковых наборов (так решили все вендоры), поэтому кратность
        // слота перемножается, а не заменяется. При quantity = 3 эспрессо нужно три, а не один.
        var demands = ComboExpander.Expand(
        [
            Bundle(Croissant, "Латте с круассаном", 3,
                Component(Espresso, "Эспрессо", quantityPerUnit: 2),
                Component(Juice, "Сок"))
        ]);

        Assert.Equal(6, demands.Single(demand => demand.ProductId == Espresso).Quantity);
        Assert.Equal(3, demands.Single(demand => demand.ProductId == Juice).Quantity);
    }

    [Fact]
    public void A_slot_of_zero_price_still_demands_the_dish()
    {
        // Бесплатный слот (0 ₽ в каталоге) не выпадает из списания: склад у блюда всё равно уходит.
        // Условие «цена == 0 → не списывать» здесь было бы прямой путём к тихому разъезду склада.
        var demands = ComboExpander.Expand(
        [
            Bundle(Croissant, "Эспрессо бесплатно", 1, Component(Espresso, "Эспрессо", unitKopecks: 0))
        ]);

        Assert.Single(demands);
        Assert.Equal(1, demands[0].Quantity);
    }

    [Fact]
    public void An_empty_cart_demands_nothing()
    {
        Assert.Empty(ComboExpander.Expand([]));
    }

    [Fact]
    public void The_demands_are_not_merged_because_the_planner_sums_them_itself()
    {
        // Две одинаковые позиции дают два требования, а не одно удвоенное. Слияние здесь означало бы
        // ещё и выбор одного из двух имён, а планировщик всё равно складывает их сам.
        var demands = ComboExpander.Expand(
        [
            Line(Espresso, "Эспрессо", quantity: 2),
            Line(Espresso, "Эспрессо", quantity: 3)
        ]);

        Assert.Equal(2, demands.Count);
        Assert.Equal(5, demands.Sum(demand => demand.Quantity));
    }

    [Fact]
    public void A_component_without_a_recipe_is_reported_once_by_name()
    {
        // Предупреждение, а не запрет: блокировка продажи превратила бы отсутствующую строку рецепта в
        // кассу, которая не может продать кофе. Молчание хуже — склад разъезжается тихо.
        var lines = new[]
        {
            Bundle(Croissant, "Латте с круассаном", 1,
                Component(Espresso, "Эспрессо"),
                Component(Juice, "Сок")),
            Bundle(Juice, "Двойной сок", 1, Component(Juice, "Сок"))
        };

        // Только у эспрессо есть рецепт, у сока — нет.
        var described = ComboExpander.DescribeComponentsWithoutRecipe(lines, new HashSet<Guid> { Espresso });

        Assert.Equal("Сок: нет рецепта — по комбо ингредиенты не спишутся", Assert.Single(described));
    }

    [Fact]
    public void Nothing_is_reported_when_every_slot_has_a_recipe()
    {
        var lines = new[]
        {
            Bundle(Croissant, "Латте с круассаном", 1,
                Component(Espresso, "Эспрессо"),
                Component(Juice, "Сок"))
        };

        var described = ComboExpander.DescribeComponentsWithoutRecipe(lines, new HashSet<Guid> { Espresso, Juice });

        Assert.Empty(described);
    }

    [Fact]
    public void An_ordinary_line_without_a_recipe_is_not_reported()
    {
        // Обычные блюда без рецепта и раньше продавались молча, и предупреждение о каждом из них
        // завалило бы список тем, что и так известно. План говорит о компонентах комбо.
        var lines = new[] { Line(Juice, "Сок") };

        var described = ComboExpander.DescribeComponentsWithoutRecipe(lines, new HashSet<Guid> { Espresso });

        Assert.Empty(described);
    }
}
