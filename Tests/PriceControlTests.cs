using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Контроль цены. Здесь две вещи: сама функция сравнения (чистая, без базы) и раздел отчёта смены
/// «Скидки», который делает её решение видимым. Первое — единственное место в приложении, где
/// сравниваются разрешённая и взимаемая цены; второе — единственное место, где результат читают.
/// </summary>
public class PriceControlTests
{
    private static readonly Guid Dish = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Side = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static CheckoutComponent Slot(long unitKopecks, long referenceKopecks, int quantityPerUnit = 1) =>
        new(Dish, "Эспрессо", quantityPerUnit, unitKopecks, referenceKopecks);

    // ─── The choke point ───

    [Fact]
    public void A_line_with_no_components_cannot_be_overridden()
    {
        // Нечего с чем сравнивать: у обычного блюда нет второго числа. Показывать такую строку как
        // скидку нельзя — именно поэтому при переносе колонки историческим строкам проставили
        // ListPriceKopecks = PriceKopecks, а не 0.
        var resolved = OrderLinePricing.Resolve([], chargedKopecks: 22000);

        Assert.Equal(22000, resolved.ListPriceKopecks);
        Assert.Equal(22000, resolved.PriceKopecks);
        Assert.False(resolved.IsOverridden);
    }

    [Fact]
    public void A_bundle_sold_at_its_own_price_is_not_overridden()
    {
        // Разрешённая цена набора — это Combo.PriceKopecks из каталога, а не сумма слотов. Сумма
        // слотов здесь 300 ₽, и она была бы взимаемой по старой модели; теперь она только ориентир.
        var resolved = OrderLinePricing.Resolve(
            [Slot(12000, 12000), Slot(18000, 18000)],
            chargedKopecks: 30000,
            bundlePriceKopecks: 30000);

        Assert.Equal(30000, resolved.ListPriceKopecks);
        Assert.False(resolved.IsOverridden);
    }

    [Fact]
    public void A_bundle_priced_above_its_parts_is_not_an_override_at_all()
    {
        // НАЦЕНКА, не пересмотр. Части стоят 300 ₽, набор объявлен за 350 ₽, и касса берёт свои 350 —
        // расхождения с разрешённой ценой нет, поэтому в раздел «Скидки» строка не попадает. Старая
        // модель объявила бы это скидкой на 50 ₽, потому что сравнивала с суммой частей.
        var resolved = OrderLinePricing.Resolve(
            [Slot(12000, 12000), Slot(18000, 18000)],
            chargedKopecks: 35000,
            bundlePriceKopecks: 35000);

        Assert.Equal(35000, resolved.ListPriceKopecks);
        Assert.False(resolved.IsOverridden);
        Assert.Equal(ComboPriceRelation.Dearer, ComboPricing.Compare(30000, 35000));
    }

    [Fact]
    public void A_bundle_charged_below_its_own_price_is_flagged()
    {
        // Ручной пересмотр не запрещён и не стирается: он просто становится виден. Сравнивается с ценой
        // набора, а не с суммой его частей.
        var resolved = OrderLinePricing.Resolve(
            [Slot(25000, 25000), Slot(30000, 30000)],
            chargedKopecks: 10000,
            bundlePriceKopecks: 55000);

        Assert.Equal(55000, resolved.ListPriceKopecks);
        Assert.Equal(10000, resolved.PriceKopecks);
        Assert.True(resolved.IsOverridden);
    }

    [Fact]
    public void A_bundle_charged_above_its_own_price_is_flagged_too()
    {
        // Наценка НА КАССЕ — тоже расхождение, и в другую сторону. Порог «только вниз» (Oracle E55) в
        // этом отчёте НЕ применяется: он приходит вместе с идентификатором оператора, а тот —
        // следующий шаг (см. план 4.2). Это не то же самое, что наценка в карточке набора: там
        // решение принято бухгалтерией заранее, здесь кто-то поднял цену на месте.
        var resolved = OrderLinePricing.Resolve([Slot(12000, 12000)], chargedKopecks: 15000, bundlePriceKopecks: 12000);

        Assert.True(resolved.IsOverridden);
        Assert.Equal(-3000, resolved.ListPriceKopecks - resolved.PriceKopecks);
    }

    [Fact]
    public void A_free_slot_no_longer_decides_the_allowed_price()
    {
        // Нулевой слот влияет ТОЛЬКО на ориентир. Раньше он обнулял часть набора и делал весь набор
        // дешевле, а теперь касса берёт цену набора, какая бы она ни была.
        var resolved = OrderLinePricing.Resolve(
            [Slot(12000, 12000), Slot(0, 3000)],
            chargedKopecks: 12000,
            bundlePriceKopecks: 12000);

        Assert.Equal(12000, resolved.ListPriceKopecks);
        Assert.False(resolved.IsOverridden);
    }

    [Fact]
    public void A_hand_built_composition_with_no_catalogue_price_is_compared_against_its_parts()
    {
        // Единственный запасной путь, и он документирован: у строки есть состав, но шаблона с ценой
        // больше нет (его удалили после продажи, строку дописали в открытый заказ). Сумма частей —
        // единственное, что сервер может сказать о такой строке, и она слабее настоящей разрешённой
        // цены. Кратность слота входит и сюда: «эспрессо ×2» — это две порции, иначе контроль
        // сравнивал бы с другим набором.
        var resolved = OrderLinePricing.Resolve(
            [Slot(12000, 12000, quantityPerUnit: 2), Slot(18000, 18000)],
            chargedKopecks: 42000);

        Assert.Equal(42000, resolved.ListPriceKopecks);
        Assert.False(resolved.IsOverridden);
    }

    // ─── The report ───

    private static async Task<(Product Latte, Guid ShiftId)> SellSomethingAsync(TestHost.Host host, decimal price)
    {
        var catalog = host.Get<ICatalogService>();
        var latte = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        await catalog.SaveProductAsync(latte);

        var shiftId = await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var order = await host.Get<ICheckoutService>().CheckoutAsync([new CheckoutLine(latte.Id, latte.Name, price, 1)]);

        return (latte, shiftId);
    }

    /// <summary>Pays a finished order and walks it to Completed — a shift report is about sales.</summary>
    private static async Task CloseAsync(IOrderService orders, Order order)
    {
        await orders.AddPaymentAsync(order.Id, order.TotalPrice, PaymentMethod.Cash);
        await orders.AdvanceStatusAsync(order.Id);   // InProgress → Ready
        await orders.AdvanceStatusAsync(order.Id);   // Ready → Completed
    }

    [Fact]
    public async Task A_sale_at_the_allowed_price_is_not_in_the_discount_report()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var (_, shiftId) = await SellSomethingAsync(host, 220m);
        var order = Assert.Single(await orders.GetActiveOrdersAsync());
        await CloseAsync(orders, order);

        // Раздел пуст не потому, что ничего не продано, а потому, что цена не трогали. Пустой раздел на
        // нормальной смене — это и есть «всё в порядке».
        Assert.Empty(await orders.GetDiscountedLinesAsync(shiftId));
    }

    [Fact]
    public async Task A_line_repriced_by_editing_the_order_shows_up_as_a_discount()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var (latte, shiftId) = await SellSomethingAsync(host, 220m);
        var order = Assert.Single(await orders.GetActiveOrdersAsync());

        // Контроль работает для ЛЮБОЙ строки, а не только для комбо: 220 → 100 это пересмотр цены.
        // И он не стирается тем, что заказ потом редактировали, потому что разрешённая цена пишется
        // один раз при создании строки и больше не переписывается. Редактировать можно только
        // заказ, который ещё готовится, поэтому пересмотр происходит ДО оплаты и закрытия.
        await orders.UpdateOrderAsync(order.Id,
        [
            new OrderItem { Id = Guid.NewGuid(), ProductId = latte.Id, ProductName = latte.Name, Price = 100m, Quantity = 1 }
        ]);

        await CloseAsync(orders, await orders.GetOrderAsync(order.Id) ?? order);

        var line = Assert.Single(await orders.GetDiscountedLinesAsync(shiftId));
        Assert.Equal(order.OrderNumber, line.OrderNumber);
        Assert.Equal("Латте", line.ProductName);
        Assert.Equal(22000, line.ListPriceKopecks);
        Assert.Equal(10000, line.PriceKopecks);
        Assert.Equal(12000, line.DiscountKopecks);   // (220 − 100) × 1
        Assert.Equal(OrderStatus.Completed, line.Status);
        // Обычная строка: второго сигнала нет, и это НЕ ноль, а отсутствие.
        Assert.False(line.IsBundle);
        Assert.Null(line.BundleSavingKopecks);
    }

    [Fact]
    public async Task A_bundles_reference_total_is_the_second_independent_signal()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = new Product { Id = Guid.NewGuid(), Name = "Эспрессо", Price = 250m, IsAvailable = true };
        var cake = new Product { Id = Guid.NewGuid(), Name = "Пирожное", Price = 300m, IsAvailable = true };
        await catalog.SaveProductAsync(espresso);
        await catalog.SaveProductAsync(cake);

        var combo = new Combo
        {
            Id = Guid.NewGuid(),
            Name = "Кофе с пирожным",
            PriceKopecks = 45000,
            Components =
            [
                new ComboComponent { Id = Guid.NewGuid(), ProductId = espresso.Id, QuantityPerUnit = 1 },
                new ComboComponent { Id = Guid.NewGuid(), ProductId = cake.Id, QuantityPerUnit = 1 }
            ]
        };
        await combos.SaveComboAsync(combo);

        var orders = host.Get<IOrderService>();
        var shiftId = await TestHost.OpenEmptyShiftAsync(orders);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
        [
            // Набор объявлен за 450 ₽, а продали за 400 ₽ — пересмотр на кассе, строка попадает в отчёт.
            // По отдельности те же блюда стоят 550 ₽, и это отдельный, независимый сигнал.
            new CheckoutLine(combo.Id, combo.Name, 400m, 2, null, null,
            [
                new CheckoutComponent(espresso.Id, espresso.Name, 1, espresso.PriceKopecks, espresso.PriceKopecks),
                new CheckoutComponent(cake.Id, cake.Name, 1, cake.PriceKopecks, cake.PriceKopecks)
            ])
        ]);

        await CloseAsync(orders, order);

        var line = Assert.Single(await orders.GetDiscountedLinesAsync(shiftId));
        Assert.Equal(45000, line.ListPriceKopecks);   // разрешено: своя цена набора, не сумма частей
        Assert.Equal(40000, line.PriceKopecks);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(10000, line.DiscountKopecks);      // (450 − 400) × 2

        // Второй, независимый сигнал: по отдельности те же блюда стоили бы 1 100 ₽ за эту строку.
        // Он не зависит от первого — «набор выгоднее своих частей» может быть и без пересмотра цены,
        // и тогда первым сигналом поймать ничего нельзя.
        Assert.True(line.IsBundle);
        Assert.Equal(110000, line.ReferenceTotalKopecks);   // (250 + 300) × 2
        Assert.Equal(80000, line.ChargedTotalKopecks);
        Assert.Equal(30000, line.BundleSavingKopecks);
    }

    [Fact]
    public async Task A_bundle_line_added_to_an_open_order_is_allowed_the_bundles_own_price()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = new Product { Id = Guid.NewGuid(), Name = "Эспрессо", Price = 250m, IsAvailable = true };
        var cake = new Product { Id = Guid.NewGuid(), Name = "Пирожное", Price = 300m, IsAvailable = true };
        await catalog.SaveProductAsync(espresso);
        await catalog.SaveProductAsync(cake);

        // Части стоят 550 ₽, набор объявлен за 500 ₽.
        var combo = new Combo
        {
            Id = Guid.NewGuid(),
            Name = "Кофе с пирожным",
            PriceKopecks = 50000,
            Components =
            [
                new ComboComponent { Id = Guid.NewGuid(), ProductId = espresso.Id, QuantityPerUnit = 1 },
                new ComboComponent { Id = Guid.NewGuid(), ProductId = cake.Id, QuantityPerUnit = 1 }
            ]
        };
        await combos.SaveComboAsync(combo);

        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [new CheckoutLine(espresso.Id, espresso.Name, 250m, 1)]);

        // ВТОРОЙ вызов Resolve: строка, добавленная в открытый заказ. Разрешённая цена обязана быть той
        // же, что дала бы касса, — иначе отчёт скидок можно обойти, не продавая, а редактируя заказ.
        await orders.UpdateOrderAsync(order.Id,
        [
            new OrderItem
            {
                Id = Guid.NewGuid(),
                ProductId = combo.Id,
                ProductName = combo.Name,
                Price = 500m,
                Quantity = 1,
                Components =
                [
                    new OrderItemComponent
                    {
                        Id = Guid.NewGuid(), ProductId = espresso.Id, ProductName = espresso.Name,
                        QuantityPerUnit = 1, UnitPriceKopecks = 25000, ReferencePriceKopecks = 25000
                    },
                    new OrderItemComponent
                    {
                        Id = Guid.NewGuid(), ProductId = cake.Id, ProductName = cake.Name,
                        QuantityPerUnit = 1, UnitPriceKopecks = 30000, ReferencePriceKopecks = 30000
                    }
                ]
            }
        ]);

        var added = Assert.Single((await orders.GetOrderAsync(order.Id))!.Items, item => item.ProductId == combo.Id);
        Assert.Equal(50000, added.PriceKopecks);
        Assert.Equal(50000, added.ListPriceKopecks);   // своя цена набора, а не 550 ₽ суммы частей
    }

    [Fact]
    public async Task The_report_returns_only_the_overridden_lines_of_that_shift()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var catalog = host.Get<ICatalogService>();

        var latte = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        var tea = new Product { Id = Guid.NewGuid(), Name = "Чай", Price = 150m, IsAvailable = true };
        await catalog.SaveProductAsync(latte);
        await catalog.SaveProductAsync(tea);

        var shiftId = await TestHost.OpenEmptyShiftAsync(orders);

        // Продать по одной цене, потом одну из них пересмотреть — единственный способ получить
        // расхождение у обычного блюда: у строки без состава нет второго числа, с которым её можно
        // сравнить (см. OrderLinePricing.Resolve).
        var repriced = await host.Get<ICheckoutService>()
            .CheckoutAsync([new CheckoutLine(latte.Id, latte.Name, 220m, 1)]);
        await orders.UpdateOrderAsync(repriced.Id,
        [
            new OrderItem { Id = Guid.NewGuid(), ProductId = latte.Id, ProductName = latte.Name, Price = 100m, Quantity = 1 }
        ]);

        var untouched = await host.Get<ICheckoutService>()
            .CheckoutAsync([new CheckoutLine(tea.Id, tea.Name, 150m, 1)]);
        var normal = await host.Get<ICheckoutService>()
            .CheckoutAsync([new CheckoutLine(latte.Id, latte.Name, 220m, 1)]);

        await CloseAsync(orders, repriced);
        await CloseAsync(orders, untouched);
        await CloseAsync(orders, normal);

        var lines = await orders.GetDiscountedLinesAsync(shiftId);

        // Ровно одна: «не тронутые» строки в раздел не попадают, иначе менеджер закрыл бы смену,
        // ничего не найдя среди сотни нормальных позиций.
        var line = Assert.Single(lines);
        Assert.Equal("Латте", line.ProductName);
        Assert.Equal(repriced.OrderNumber, line.OrderNumber);
        Assert.Equal(12000, line.DiscountKopecks);
    }
}
