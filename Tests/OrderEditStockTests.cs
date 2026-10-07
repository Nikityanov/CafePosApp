using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.EntityFrameworkCore;
using static CafePosApp.Tests.OrderPaymentHarness;

namespace CafePosApp.Tests;

/// <summary>
/// Правка заказа и склад. До этой правки <c>UpdateOrderAsync</c> не писал ни одного движения и не
/// проверял наличие: <c>StockMovement</c> появлялся только на чекауте и на поставке.
///
/// Интеграционные тесты против настоящей SQLite, а не чистые: половина проверяемого — это
/// транзакция и то, что журнал заказа и сам заказ не могут разойтись на полпути.
/// </summary>
public class OrderEditStockTests
{
    /// <summary>Блюдо со своим ингредиентом: два независимых склада, чтобы движение по одному нельзя было спутать с движением по другому.</summary>
    private static async Task<(Product Product, Ingredient Ingredient)> DishAsync(
        ICatalogService catalog,
        string dish,
        string ingredient,
        decimal price,
        decimal perServing,
        decimal stock)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = dish, Price = price, IsAvailable = true };
        await catalog.SaveProductAsync(product);

        var item = new Ingredient
        {
            Id = Guid.NewGuid(),
            Name = ingredient,
            Unit = "мл",
            CostPerUnit = 0.06m,
            StockQuantity = stock,
            MinStockLevel = 100m,
            IsAvailable = true
        };
        await catalog.SaveIngredientAsync(item);

        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            IngredientId = item.Id,
            Quantity = perServing
        });

        return (product, item);
    }

    /// <summary>Строка заказа в том виде, в каком её отдаёт редактор заказа.</summary>
    private static OrderItem Line(Product product, decimal price, int quantity, params OrderItemComponent[] components) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = product.Id,
        ProductName = product.Name,
        Price = price,
        Quantity = quantity,
        Components = [.. components]
    };

    private static async Task<List<StockMovement>> JournalAsync(TestHost.Host host, Guid orderId)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.StockMovements.AsNoTracking()
            .Where(row => row.OrderId == orderId)
            .ToListAsync();
    }

    [Fact]
    public async Task Editing_a_quantity_down_puts_the_ingredient_back()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte, 2);
        Assert.Equal(MilkStock - 400m, await StockOfAsync(host, milk.Id));

        await orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 1)]);

        // 200 мл вернулись на полку: заказ ещё «В работе», ничего не налито, и ингредиент стоит
        // ровно там, где стоял бы, если бы чашку вообще не набирали.
        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));

        var edit = Assert.Single(await JournalAsync(host, order.Id), row => row.Reason.StartsWith("Правка заказа"));
        Assert.Equal(MilkPerLatte, edit.QuantityDelta);
    }

    [Fact]
    public async Task Editing_a_quantity_up_writes_the_ingredient_off()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);
        await orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 3)]);

        Assert.Equal(MilkStock - 600m, await StockOfAsync(host, milk.Id));
        Assert.Equal(-400m, Assert.Single(await JournalAsync(host, order.Id), row => row.Reason.StartsWith("Правка заказа")).QuantityDelta);
    }

    [Fact]
    public async Task Removing_a_line_returns_what_that_line_was_written_off_for()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);
        var (tea, water) = await DishAsync(catalog, "Чай", "Вода", 150m, 250m, 50000m);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
        [
            new CheckoutLine(latte.Id, latte.Name, LattePrice, 1),
            new CheckoutLine(tea.Id, tea.Name, 150m, 1)
        ]);

        await orders.UpdateOrderAsync(order.Id, [Line(tea, 150m, 1)]);

        // Только молоко вернулось. Вода осталась списанной — чай из неё не убрали.
        Assert.Equal(MilkStock, await StockOfAsync(host, milk.Id));
        Assert.Equal(50000m - 250m, await StockOfAsync(host, water.Id));
    }

    [Fact]
    public async Task Adding_a_line_writes_off_what_its_own_recipe_says()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, _) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);
        var (tea, water) = await DishAsync(catalog, "Чай", "Вода", 150m, 250m, 50000m);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);
        await orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 1), Line(tea, 150m, 2)]);

        Assert.Equal(50000m - 500m, await StockOfAsync(host, water.Id));
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_no_movement()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);
        await orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 1)]);

        // Пересохранение без изменений — не событие. Пара «−200, +200» в журнале выглядела бы как
        // движение и делала бы отчёт о складе враньём при нулевом изменении остатка.
        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));
        Assert.Single(await JournalAsync(host, order.Id));
    }

    [Fact]
    public async Task Editing_a_quantity_up_refuses_when_the_ingredient_is_gone()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        // Ровно одна порция на полке: после чекаута не остаётся ничего.
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkPerLatte);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);
        Assert.Equal(0m, await StockOfAsync(host, milk.Id));

        await Assert.ThrowsAsync<InsufficientStockException>(
            () => orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 2)]));

        // Отказ — это отказ ЦЕЛИКОМ. Заказ остался тем, чем был: одна строка, одна чашка.
        Assert.Equal(0m, await StockOfAsync(host, milk.Id));
        var item = Assert.Single((await orders.GetOrderAsync(order.Id))!.Items);
        Assert.Equal(1, item.Quantity);
        Assert.Single(await JournalAsync(host, order.Id));
    }

    /// <summary>
    /// То, ради чего правка вообще нужна: после неё отмена обязана вернуть ровно то, что заказ
    /// съел ПОСЛЕ правки. Возврат читает журнал, поэтому несовпадение журнала и заказа — это не
    /// косметика, а деньги на полке.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_edited_order_returns_only_what_the_edited_order_took()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte, 2);
        await orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 1)]);
        await orders.CancelOrderAsync(order.Id, stock: StockDisposition.ReturnToStock);

        // Полка вернулась к исходной. Без списания при правке сюда вернулось бы 400 мл за одну
        // выпитую чашку, и остаток в каталоге разошёлся бы с остатком на полке навсегда.
        Assert.Equal(MilkStock, await StockOfAsync(host, milk.Id));

        var net = (await JournalAsync(host, order.Id)).Sum(row => row.QuantityDelta);
        Assert.Equal(0m, net);
    }

    [Fact]
    public async Task Editing_an_order_whose_journal_carries_a_delivery_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);

        // Поставка, записанная на этот заказ. Через приложение невозможна, руками — да.
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.StockMovements.Add(new StockMovement
            {
                IngredientId = milk.Id,
                QuantityDelta = 500m,
                StockAfter = MilkStock + 500m,
                Reason = "Поставка по заказу",
                OrderId = order.Id,
                CreatedAt = DateTimeOffset.Parse("2026-10-07T09:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture)
            });
            await db.SaveChangesAsync();
        }

        // Журнал перестал быть «сколько съел этот заказ», и считать дельту по нему — значит
        // придумать число. Отказ называет причину вместо тихой правки полки.
        await Assert.ThrowsAsync<ConflictException>(
            () => orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 2)]));

        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));
        Assert.Equal(1, Assert.Single((await orders.GetOrderAsync(order.Id))!.Items).Quantity);
    }

    /// <summary>
    /// Рецепт правится без версий, поэтому «сколько съел заказ» берётся из журнала, а «сколько
    /// нужно теперь» — из сегодняшнего рецепта. Если рецепт уехал между оформлением и правкой,
    /// эти два числа описывают разные времена, и складывать их нельзя: получился бы возврат
    /// молока, которого с полки не снимали. Отказ вместо вранья.
    /// </summary>
    [Fact]
    public async Task Editing_refuses_when_the_recipe_changed_after_checkout()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();
        var (latte, milk) = await DishAsync(catalog, "Латте", "Молоко", LattePrice, MilkPerLatte, MilkStock);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), latte);

        // Бар меняет рецепт после продажи: чашка теперь 500 мл вместо 200.
        var recipeItem = (await catalog.GetRecipeItemsByProductAsync(latte.Id)).Single();
        recipeItem.Quantity = 500m;
        await catalog.SaveRecipeItemAsync(recipeItem);

        await Assert.ThrowsAsync<ConflictException>(
            () => orders.UpdateOrderAsync(order.Id, [Line(latte, LattePrice, 2)]));

        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));
        Assert.Equal(1, Assert.Single((await orders.GetOrderAsync(order.Id))!.Items).Quantity);
    }

    [Fact]
    public async Task An_order_whose_dish_has_no_recipe_can_still_be_edited()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();

        // Без рецепта чекаут не пишет списания, а правило «без рецепта продаём с предупреждением»
        // должно работать и на правке: пустой журдж — это ноль, а не отсутствие данных.
        var water = new Product { Id = Guid.NewGuid(), Name = "Вода", Price = 60m, IsAvailable = true };
        await catalog.SaveProductAsync(water);

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), water);
        await orders.UpdateOrderAsync(order.Id, [Line(water, 60m, 3)]);

        Assert.Empty(await JournalAsync(host, order.Id));
        Assert.Equal(3, Assert.Single((await orders.GetOrderAsync(order.Id))!.Items).Quantity);
    }

    [Fact]
    public async Task Editing_a_bundle_line_adjusts_the_stock_of_its_components()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var orders = host.Get<IOrderService>();

        var (espresso, beans) = await DishAsync(catalog, "Эспрессо", "Зерно", 120m, 20m, 1000m);
        // Круассан без рецепта: склад по нему не ведётся, и на правке он тоже не должен появляться.
        var croissant = new Product { Id = Guid.NewGuid(), Name = "Круассан", Price = 180m, IsAvailable = true };
        await catalog.SaveProductAsync(croissant);

        var combo = new Combo
        {
            Id = Guid.NewGuid(),
            Name = "Эспрессо с круассаном",
            PriceKopecks = 28000,
            SortOrder = 0,
            Components =
            [
                new ComboComponent { Id = Guid.NewGuid(), ProductId = espresso.Id, QuantityPerUnit = 1 },
                new ComboComponent { Id = Guid.NewGuid(), ProductId = croissant.Id, QuantityPerUnit = 1 }
            ]
        };
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [
                new CheckoutLine(
                    combo.Id,
                    combo.Name,
                    280m,
                    1,
                    null,
                    null,
                    [
                        new CheckoutComponent(espresso.Id, espresso.Name, 1, espresso.PriceKopecks, espresso.PriceKopecks),
                        new CheckoutComponent(croissant.Id, croissant.Name, 1, croissant.PriceKopecks, croissant.PriceKopecks)
                    ])
            ]);

        Assert.Equal(1000m - 20m, await StockOfAsync(host, beans.Id));

        await orders.UpdateOrderAsync(order.Id,
        [
            new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = combo.Id,
                ProductName = combo.Name,
                Price = 280m,
                Quantity = 2,
                Components =
                [
                    new OrderItemComponent { Id = Guid.NewGuid(), ProductId = espresso.Id, ProductName = espresso.Name, QuantityPerUnit = 1 },
                    new OrderItemComponent { Id = Guid.NewGuid(), ProductId = croissant.Id, ProductName = croissant.Name, QuantityPerUnit = 1 }
                ]
            }
        ]);

        // Слот набора — это и есть то, что списывается, поэтому количество 2 на строке комбо
        // означает два полных набора, а не два слота.
        Assert.Equal(1000m - 40m, await StockOfAsync(host, beans.Id));
    }
}
