using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Продажа комбо целиком: одна строка заказа, цена — СВОЯ цена набора, состав — снимок из каталога,
/// ингредиенты списываются по слотам. Интеграционные тесты против настоящей SQLite, потому что
/// половина того, что здесь проверяется, — это взаимодействие EF с транзакцией, и чистая функция его
/// не покрывает.
/// <para>
/// Тесты переписаны под модель, в которой у комбо есть собственная цена. Раньше здесь проверялось
/// «цена = сумма состава» — это было верно только потому, что другой цены не существовало, и теперь
/// это ложь по замыслу.
/// </para>
/// </summary>
public class ComboCheckoutTests
{
    private static async Task<Product> DishAsync(ICatalogService catalog, string name, decimal price, bool available = true)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = name, Price = price, IsAvailable = available };
        await catalog.SaveProductAsync(product);
        return product;
    }

    /// <summary>The price is part of the card, exactly as it is on the real form.</summary>
    private static Combo Bundle(string name, long priceKopecks, params ComboComponent[] slots) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PriceKopecks = priceKopecks,
        SortOrder = 0,
        Components = [.. slots]
    };

    private static ComboComponent Slot(Product product, long? priceKopecks = null, Product? substitute = null) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = product.Id,
        QuantityPerUnit = 1,
        ComponentPriceKopecks = priceKopecks,
        SubstituteProductId = substitute?.Id
    };

    /// <summary>
    /// A cart line for a bundle, built the way the menu screen would build it — the client sends the
    /// bundle's own price, which is the whole point of the reversal.
    /// </summary>
    private static CheckoutLine BundleLine(Combo combo, decimal price, int quantity = 1, params CheckoutComponent[] components) =>
        new(combo.Id, combo.Name, price, quantity, null, null, [.. components]);

    private static CheckoutComponent Asked(Product product) =>
        new(product.Id, product.Name, 1, Money(product), Money(product));

    private static long Money(Product product) => product.PriceKopecks;

    [Fact]
    public async Task A_bundle_lands_as_one_order_line_priced_at_its_own_price()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);

        // Собственная цена 280 ₽ при сумме частей 300 ₽: набор дешевле своих частей, и это решение
        // бухгалтерии, а не результат сложения.
        var combo = Bundle("Латте с круассаном", 28000, Slot(espresso), Slot(croissant));
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 280m, 1, Asked(espresso), Asked(croissant))]);

        // ONE line, priced at the bundle's own 280 ₽ — NOT at the 300 ₽ the slots add up to. Не «строка
        // на слот»: количество N на строке комбо означает N полных наборов, иначе три одинаковых
        // набора стали бы тремя строками с разными ценами.
        var item = Assert.Single(order.Items);
        Assert.Equal(combo.Id, item.ProductId);
        Assert.Equal(28000, item.PriceKopecks);
        Assert.Equal(28000, item.ListPriceKopecks);   // разрешено — та же цифра, пересмотра не было
        Assert.Equal([espresso.Id, croissant.Id], item.Components.OrderBy(c => c.SortOrder).Select(c => c.ProductId));
        Assert.Equal(280m, order.TotalPrice);
    }

    [Fact]
    public async Task The_order_total_still_is_the_sum_of_price_times_quantity()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var combo = Bundle("Двойной эспрессо", 12000, Slot(espresso));
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 120m, quantity: 3, Asked(espresso))]);

        // Инвариант заказа не изменился из-за появления собственной цены у комбо: Σ(PriceKopecks × Quantity).
        var item = Assert.Single(order.Items);
        Assert.Equal(12000, item.PriceKopecks);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(36000, order.TotalKopecks);
        Assert.Equal(360m, order.TotalPrice);
    }

    [Fact]
    public async Task The_composition_snapshot_is_written_with_both_prices()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var free = await DishAsync(catalog, "Сахар", 30m);
        var combo = Bundle("Кофе с сахаром", 12000, Slot(espresso), Slot(free, priceKopecks: 0));
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 120m, 1, Asked(espresso), Asked(free))]);

        var components = order.Items[0].Components.OrderBy(component => component.SortOrder).ToList();

        // Обе цены обязательны: UnitPrice — сколько слот стоит в ОРИЕНТИРЕ набора, ReferencePrice —
        // сколько то же блюдо стоит отдельно. Второе и есть то, против чего сравнивается набор.
        Assert.Equal(12000, components[0].UnitPriceKopecks);
        Assert.Equal(12000, components[0].ReferencePriceKopecks);
        Assert.Equal(0, components[1].UnitPriceKopecks);
        Assert.Equal(3000, components[1].ReferencePriceKopecks);

        // И снимок переживает перезагрузку: ProductId здесь НЕ внешний ключ.
        var reloaded = await host.Get<IOrderService>().GetOrderAsync(order.Id);
        Assert.Equal(
            [espresso.Id, free.Id],
            reloaded!.Items[0].Components.OrderBy(c => c.SortOrder).Select(c => c.ProductId));
    }

    [Fact]
    public async Task A_bundle_priced_above_its_parts_is_sold_at_its_own_price_and_is_not_a_discount()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var shiftId = await TestHost.OpenEmptyShiftAsync(orders);
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var cake = await DishAsync(catalog, "Пирожное", 180m);

        // НАЦЕНКА: части стоят 300 ₽, набор объявлен за 330 ₽. Это законно, это не пересмотр и не
        // скидка — и касса берёт именно 330 ₽.
        var combo = Bundle("Кофе с пирожным", 33000, Slot(espresso), Slot(cake));
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 330m, 1, Asked(espresso), Asked(cake))]);

        var item = Assert.Single(order.Items);
        Assert.Equal(33000, item.PriceKopecks);
        Assert.Equal(33000, item.ListPriceKopecks);   // своя цена = взимаемая, расхождения нет
        Assert.Equal(330m, order.TotalPrice);

        // И в отчёте смены её нет: раздел «Скидки» фильтрует по расхождению цены, а не по сравнению с
        // суммой частей. Наценка в карточке — решение бухгалтерии, а не выданное на кассе.
        await orders.AddPaymentAsync(order.Id, order.TotalPrice, PaymentMethod.Cash);
        await orders.AdvanceStatusAsync(order.Id);
        await orders.AdvanceStatusAsync(order.Id);
        Assert.Empty(await orders.GetDiscountedLinesAsync(shiftId));
    }

    [Fact]
    public async Task A_client_price_that_contradicts_the_bundles_own_price_is_charged_as_sent_and_flagged()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 250m);
        var cake = await DishAsync(catalog, "Пирожное", 300m);
        var combo = Bundle("Кофе с пирожным", 50000, Slot(espresso), Slot(cake));
        await combos.SaveComboAsync(combo);

        // Клиент прислал 1,00 ₽ за набор, чья карточка стоит 500 ₽. Ручной пересмотр — разрешённое
        // действие, поэтому сумма берётся как прислана, и именно поэтому разница обязана быть видна.
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 1.00m, 1, Asked(espresso), Asked(cake))]);

        var item = Assert.Single(order.Items);
        Assert.Equal(100, item.PriceKopecks);           // что взимается
        Assert.Equal(50000, item.ListPriceKopecks);     // что разрешено — своя цена из каталога
        Assert.Equal(100, order.TotalKopecks);
    }

    [Fact]
    public async Task A_forged_composition_and_price_cannot_beat_the_catalogues_price()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 250m);
        var cake = await DishAsync(catalog, "Пирожное", 300m);
        var combo = Bundle("Кофе с пирожным", 50000, Slot(espresso), Slot(cake));
        await combos.SaveComboAsync(combo);

        // Обход, ради которого всё и затевалось: клиент присылает состав, который в сумме равен его же
        // цене, и тогда старая проверка «цена = сумма компонентов» выполняется. Состав пересобирается
        // сервером из каталога, а разрешённая цена читается из карточки набора — подделать нельзя, и
        // иначе контроль не значил бы ничего.
        var forged = new CheckoutComponent(espresso.Id, "Эспрессо", 1, UnitPriceKopecks: 50000, ReferencePriceKopecks: 50000);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 500m, 1, forged, Asked(cake))]);

        var item = Assert.Single(order.Items);
        Assert.Equal(50000, item.ListPriceKopecks);
        // Имена/цены слотов — из каталога, а не из корзины.
        Assert.Equal([25000, 30000], item.Components.OrderBy(c => c.SortOrder).Select(c => c.UnitPriceKopecks));
    }

    [Fact]
    public async Task A_composition_the_catalogue_no_longer_has_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var combo = Bundle("Кофе", 12000, Slot(espresso));
        await combos.SaveComboAsync(combo);

        // Состав, которого в каталоге нет — это не «продай как есть», а «обнови экран»: принять его
        // значило бы позволить корзине выдумать набор, которого никто не каталогизировал.
        var stale = new CheckoutComponent(Guid.NewGuid(), "Что-то", 1, 100, 100);
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync([BundleLine(combo, 120m, 1, stale)]));

        Assert.Contains("Состав комбо", failure.Message);
        Assert.Empty(await host.Get<IOrderService>().GetActiveOrdersAsync());
    }

    [Fact]
    public async Task An_unavailable_slot_sells_its_substitute_and_the_price_does_not_move()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var decaf = await DishAsync(catalog, "Без кофеина", 200m);
        var combo = Bundle("Латте", 12000, Slot(espresso, substitute: decaf));
        await combos.SaveComboAsync(combo);

        // Эспрессо недоступен — но замена есть, поэтому это НЕ отказ. Подмена вместо блокировки так
        // делают все вендоры (Simphony substitution groups, D365 substitutes). D365 при этом ещё и
        // пересчитывает цену кита, если замена дороже, — и ЭТО ЧАСТЬ ИЗМЕНЕНИЯ: при фиксированной цене
        // набора разницу поглощает кафе, а не клиент. Набор стоит 120 ₽ и стоит 120 ₽.
        await catalog.ToggleProductAvailabilityAsync(espresso.Id);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 120m, 1, Asked(espresso))]);

        var item = Assert.Single(order.Items);
        Assert.Equal(12000, item.PriceKopecks);
        Assert.Equal(12000, item.ListPriceKopecks);   // подмена не объявила себя пересмотром цены
        Assert.Equal(120m, order.TotalPrice);

        // А ЧТО ПРОДАЛИ — видно: замена пишется в снимок строки со своей ценой, потому что кухня
        // печатает оттуда, а проверка через полгода должна знать, что налили.
        var component = Assert.Single(item.Components);
        Assert.Equal(decaf.Id, component.ProductId);
        // Имя на чеке — имя того, что ПРОДАНО, а не того, что закончилось.
        Assert.Equal("Без кофеина", component.ProductName);
        Assert.Equal(20000, component.ReferencePriceKopecks);
    }

    [Fact]
    public async Task An_unavailable_slot_with_no_substitute_is_refused_by_name()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);
        var combo = Bundle("Латте с круассаном", 28000, Slot(espresso), Slot(croissant));
        await combos.SaveComboAsync(combo);

        await catalog.ToggleProductAvailabilityAsync(espresso.Id);

        // Продажа блокируется, и сообщение НАЗЫВАЕТ блюдо: «комбо недоступно» заставило бы оператора
        // перебирать состав, гадая, какой из четырёх слотов виноват.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync(
                [BundleLine(combo, 280m, 1, Asked(espresso), Asked(croissant))]));

        Assert.Contains("Эспрессо", failure.Message);
        Assert.Contains("замена не настроена", failure.Message);
        Assert.Empty(await host.Get<IOrderService>().GetActiveOrdersAsync());
    }

    [Fact]
    public async Task A_slot_whose_substitute_is_also_gone_says_so()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var decaf = await DishAsync(catalog, "Без кофеина", 200m);
        var combo = Bundle("Латте", 12000, Slot(espresso, substitute: decaf));
        await combos.SaveComboAsync(combo);

        await catalog.ToggleProductAvailabilityAsync(espresso.Id);
        await catalog.ToggleProductAvailabilityAsync(decaf.Id);

        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync([BundleLine(combo, 120m, 1, Asked(espresso))]));

        Assert.Contains("Эспрессо", failure.Message);
        Assert.Contains("тоже недоступна", failure.Message);
    }

    [Fact]
    public async Task A_free_slot_costs_nothing_in_the_reference_and_the_bundle_still_costs_its_own_price()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var syrup = await DishAsync(catalog, "Сироп", 90m);
        var caramel = await DishAsync(catalog, "Карамель", 150m);
        var combo = Bundle("Кофе с сиропом", 9000, Slot(syrup, priceKopecks: 0, substitute: caramel));
        await combos.SaveComboAsync(combo);

        await catalog.ToggleProductAvailabilityAsync(syrup.Id);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 90m, 1, Asked(syrup))]);

        // Слот за 0 ₽ остаётся нулевым в ОРИЕНТИРЕ даже после подмены (план 1.6, случай 3) — но он
        // больше не делает набор бесплатным: недоступность слота влияет на возможность продажи, а цену
        // набора определяет карточка. Раньше здесь ожидался ноль в счёте.
        Assert.Equal(0, Assert.Single(order.Items).Components[0].UnitPriceKopecks);
        Assert.Equal(9000, order.TotalKopecks);
    }

    [Fact]
    public async Task A_free_slot_with_no_substitute_still_blocks_the_sale()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var syrup = await DishAsync(catalog, "Сироп", 90m);
        var combo = Bundle("Кофе с сиропом", 9000, Slot(syrup, priceKopecks: 0));
        await combos.SaveComboAsync(combo);

        await catalog.ToggleProductAvailabilityAsync(syrup.Id);

        // Цена тут не при чём: слот бесплатный в ориентире, и недоступность не должна была бы мешать. Но
        // продавать набор, часть которого нельзя положить в чашку, нельзя — поэтому отказ тот же.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync([BundleLine(combo, 90m, 1, Asked(syrup))]));

        Assert.Contains("Сироп", failure.Message);
    }

    [Fact]
    public async Task Ingredients_are_written_off_through_the_bundle_components()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var milk = await IngredientAsync(catalog, "Молоко", 1000m);
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = espresso.Id,
            IngredientId = milk.Id,
            Quantity = 200m
        });

        // У САМОГО комбо рецепта нет: он не блюдо, а свёртка. Если бы списание шло по строке заказа,
        // молоко не списалось бы ВООБЩЕ — и это молчаливый разъезд склада.
        var combo = Bundle("Эспрессо", 12000, Slot(espresso));
        await combos.SaveComboAsync(combo);

        await host.Get<ICheckoutService>().CheckoutAsync([BundleLine(combo, 120m, quantity: 2, Asked(espresso))]);

        var ingredient = await catalog.GetIngredientAsync(milk.Id);
        Assert.Equal(600m, ingredient!.StockQuantity);   // 2 набора × 200 мл
    }

    [Fact]
    public async Task The_same_dish_in_two_slots_is_written_off_twice_not_once()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var milk = await IngredientAsync(catalog, "Молоко", 1000m);
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = espresso.Id,
            IngredientId = milk.Id,
            Quantity = 200m
        });

        // Два слота одного блюда — это две порции, и склад должен узнать про обе. «Перезаписать» вместо
        // сложения списало бы одну и склад разъехался ровно на amount второй.
        var combo = Bundle("Двойной", 24000, Slot(espresso), Slot(espresso));
        await combos.SaveComboAsync(combo);

        await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 240m, 1, Asked(espresso), Asked(espresso))]);

        var ingredient = await catalog.GetIngredientAsync(milk.Id);
        Assert.Equal(600m, ingredient!.StockQuantity);   // 2 слота × 200 мл, а не 200
    }

    [Fact]
    public async Task A_component_without_a_recipe_is_still_sold()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var milk = await IngredientAsync(catalog, "Молоко", 1000m);
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var water = await DishAsync(catalog, "Вода", 0m);      // рецепта нет и не будет
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = espresso.Id,
            IngredientId = milk.Id,
            Quantity = 200m
        });

        var combo = Bundle("Эспрессо с водой", 12000, Slot(espresso), Slot(water));
        await combos.SaveComboAsync(combo);

        // Предупреждение, а не запрет: отсутствующий рецепт — это огрех каталога, и запрет превратил бы
        // его в кассу, которая не может продавать кофе. Молчание — вот что действительно вредит.
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [BundleLine(combo, 120m, 1, Asked(espresso), Asked(water))]);

        Assert.Single(order.Items);
        var ingredient = await catalog.GetIngredientAsync(milk.Id);
        Assert.Equal(800m, ingredient!.StockQuantity);
    }

    private static async Task<Ingredient> IngredientAsync(ICatalogService catalog, string name, decimal stock)
    {
        var ingredient = new Ingredient
        {
            Id = Guid.NewGuid(),
            Name = name,
            Unit = "мл",
            StockQuantity = stock,
            IsAvailable = true
        };
        await catalog.SaveIngredientAsync(ingredient);
        return ingredient;
    }
}
