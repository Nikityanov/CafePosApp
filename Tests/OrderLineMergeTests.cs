using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Ключ склейки строк на живом заказе. Чистая функция <see cref="Common.OrderLineKey"/> проверяется в
/// <c>OrderLineKeyTests</c>; здесь важно другое — что сервис заказа действительно ею пользуется, потому
/// что инлайновое сравнение в трёх местах уже разошлось один раз (вариант в нём вообще не
/// участвовал), и именно поэтому оно вынесено в ядро.
/// </summary>
public class OrderLineMergeTests
{
    private static async Task<Product> DishAsync(ICatalogService host, string name, decimal price)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = name, Price = price, IsAvailable = true };
        await host.SaveProductAsync(product);
        return product;
    }

    /// <summary>A cart line for a bundle, as the order editor would hand it over.</summary>
    private static OrderItem Line(Product product, decimal price, int quantity, params OrderItemComponent[] components) =>
        Line(product.Id, product.Name, price, quantity, components);

    /// <summary>
    /// A bundle line: its identity is the BUNDLE, not the first dish in it. Getting that wrong is not a
    /// detail — the key would not match the line the sale created and every save would replace it.
    /// </summary>
    private static OrderItem Line(Guid productId, string productName, decimal price, int quantity, params OrderItemComponent[] components) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = productId,
        ProductName = productName,
        Price = price,
        Quantity = quantity,
        Components = [.. components]
    };

    private static OrderItemComponent Part(Product product, int quantityPerUnit = 1) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = product.Id,
        ProductName = product.Name,
        QuantityPerUnit = quantityPerUnit,
        UnitPriceKopecks = product.PriceKopecks,
        ReferencePriceKopecks = product.PriceKopecks
    };

    [Fact]
    public async Task Two_identical_custom_builds_of_a_bundle_merge_into_one_line()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [new CheckoutLine(espresso.Id, espresso.Name, 120m, 1)]);

        // Состав, набранный в обратном порядке, — тот же набор. Подпись ключа сортируется по ProductId
        // специально ради этого: без сортировки два одинаковых по смыслу комбо разъехались бы по разным
        // строкам, и количество у каждой было бы 1 вместо 2.
        await orders.UpdateOrderAsync(order.Id,
        [
            Line(espresso, 300m, 1, Part(espresso), Part(croissant)),
            Line(espresso, 300m, 2, Part(croissant), Part(espresso))
        ]);

        var reloaded = await orders.GetOrderAsync(order.Id);

        // ОДНА строка. Раньше их становилось две: строка, добавленная ранее в этом же вызове, ещё не
        // попала в загруженную коллекцию, поэтому две одинаковые не находили друг друга.
        var item = Assert.Single(reloaded!.Items);

        // Количество — последнее высказанное вызывающим, а не сумма: набор строк заказа передаётся
        // целиком и вызывающий в нём authoritative (так же работала и прежняя реализация, и менять
        // это здесь незачем). Настоящая корзина двух одинаковых строк не присылает — она держит
        // количество в одной строке, — так что это защита от задвоения, а не арифметика.
        Assert.Equal(2, item.Quantity);
        Assert.Equal(60000, item.LineTotalKopecks);   // 2 набора × 300 ₽

        // И состав — РОВНО два слота. Регрессия на ловушку EF: добавление строки через DbSet к уже
        // отслеживаемой родительской сущности само чинит обратную навигацию, и ручное добавление в
        // коллекцию оставляло каждый слот в ней дважды — а удвоенный состав идёт в ключ склейки.
        Assert.Equal(2, item.Components.Count);
    }

    [Fact]
    public async Task Two_different_builds_of_the_same_bundle_stay_apart()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);
        var juice = await DishAsync(catalog, "Сок", 150m);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [new CheckoutLine(espresso.Id, espresso.Name, 120m, 1)]);

        // Состав — часть идентичности строки. Без него два набора из одних и тех же блюд, но разные,
        // склеились бы в одну стрку по цене той, что добавили первой, и вторая просто исчезла бы из чека.
        await orders.UpdateOrderAsync(order.Id,
        [
            Line(espresso, 300m, 1, Part(espresso), Part(croissant)),
            Line(espresso, 450m, 1, Part(espresso), Part(croissant), Part(juice))
        ]);

        var reloaded = await orders.GetOrderAsync(order.Id);

        Assert.Equal(2, reloaded!.Items.Count);
        Assert.Equal([300m, 450m], reloaded.Items.Select(item => item.Price).OrderBy(price => price));
        Assert.Equal(750m, reloaded.TotalPrice);
    }

    [Fact]
    public async Task The_same_bundle_dish_and_a_plain_dish_are_two_lines()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [new CheckoutLine(espresso.Id, espresso.Name, 120m, 1)]);

        await orders.UpdateOrderAsync(order.Id,
        [
            Line(espresso, 120m, 1),
            Line(espresso, 120m, 1, Part(espresso))
        ]);

        var reloaded = await orders.GetOrderAsync(order.Id);

        // Пустой состав даёт пустую подпись, а не «состав из одного и того же блюда»: обычная порция и
        // набор — разные позиции с разными ценами.
        Assert.Equal(2, reloaded!.Items.Count);
    }

    [Fact]
    public async Task The_variant_is_part_of_the_key_so_two_sizes_do_not_merge()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var orders = host.Get<IOrderService>();

        var latte = await DishAsync(catalog, "Латте", 220m);
        latte.HasVariants = true;
        latte.Variants =
        [
            new ProductVariant { Id = Guid.NewGuid(), Name = "Большой", Price = 350m },
            new ProductVariant { Id = Guid.NewGuid(), Name = "Средний", Price = 300m }
        ];
        await catalog.SaveProductAsync(latte);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [new CheckoutLine(latte.Id, latte.Name, 220m, 1)]);

        var big = Line(latte, 350m, 1);
        big.SelectedVariantName = "Большой";
        var medium = Line(latte, 300m, 1);
        medium.SelectedVariantName = "Средний";

        await orders.UpdateOrderAsync(order.Id, [big, medium]);

        var reloaded = await orders.GetOrderAsync(order.Id);

        // Регрессия на то самое расхождение, ради которого ключ и выносили в ядро: вариант в ключе
        // должен быть, иначе две чашки разного объёма сложатся в одну стрку по одной из цен.
        Assert.Equal(2, reloaded!.Items.Count);
        Assert.Equal(650m, reloaded.TotalPrice);
    }

    [Fact]
    public async Task Editing_a_line_keeps_its_identifier_and_its_allowed_price()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var orders = host.Get<IOrderService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);

        // Настоящее комбо в каталоге, а не собранное на лету: состав на кассе всегда сверяется с
        // шаблоном, и строка без шаблона отвергается (ComboService.ResolveSaleCompositionsAsync).
        var combo = new Combo
        {
            Id = Guid.NewGuid(),
            Name = "Набор",
            PriceKopecks = 30000,
            Components =
            [
                new ComboComponent { Id = Guid.NewGuid(), ProductId = espresso.Id, QuantityPerUnit = 1 },
                new ComboComponent { Id = Guid.NewGuid(), ProductId = croissant.Id, QuantityPerUnit = 1 }
            ]
        };
        await combos.SaveComboAsync(combo);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
        [
            new CheckoutLine(combo.Id, combo.Name, 300m, 1, null, null,
            [
                new CheckoutComponent(espresso.Id, espresso.Name, 1, espresso.PriceKopecks, espresso.PriceKopecks),
                new CheckoutComponent(croissant.Id, croissant.Name, 1, croissant.PriceKopecks, croissant.PriceKopecks)
            ])
        ]);

        var before = Assert.Single((await orders.GetOrderAsync(order.Id))!.Items);

        await orders.UpdateOrderAsync(order.Id,
        [
            // Та же строка (тот же ключ: тот же набор и тот же состав), но количество и цена другие.
            Line(combo.Id, combo.Name, 400m, 3, Part(espresso), Part(croissant))
        ]);

        var after = Assert.Single((await orders.GetOrderAsync(order.Id))!.Items);

        // Идентификатор строки пережил правку: по нему печатается чек, и пересоздавать его — значит
        // терять связь с аудитом.
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(3, after.Quantity);
        // Разрешённая цена ПЕРЕЖИЛА правку — в этом весь смысл раздела «Скидки»: пересмотренная цена
        // обязана остаться видна, а не подменить собой «как разрешено».
        Assert.Equal(30000, after.ListPriceKopecks);
        Assert.Equal(40000, after.PriceKopecks);
        Assert.Equal(120000, after.LineTotalKopecks);
    }
}
