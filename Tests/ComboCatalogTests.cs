using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Каталог комбо: проверки перед сохранением и ПОЛНАЯ замена состава при правке. Последнее — не
/// удобство, а единственная безопасная форма записи, и именно поэтому оно проверяется отдельно.
/// </summary>
public class ComboCatalogTests
{
    private static async Task<Product> DishAsync(ICatalogService catalog, string name, decimal price, bool available = true)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = name, Price = price, IsAvailable = available };
        await catalog.SaveProductAsync(product);
        return product;
    }

    /// <summary>The price is part of the card, exactly as it is on the real form.</summary>
    private static Combo Bundle(string name, long priceKopecks = 15000, params ComboComponent[] slots) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PriceKopecks = priceKopecks,
        SortOrder = 0,
        Components = [.. slots]
    };

    private static ComboComponent Slot(Product product, long? priceKopecks = null, int quantityPerUnit = 1, Product? substitute = null) => new()
    {
        Id = Guid.NewGuid(),
        ProductId = product.Id,
        QuantityPerUnit = quantityPerUnit,
        ComponentPriceKopecks = priceKopecks,
        SubstituteProductId = substitute?.Id
    };

    [Fact]
    public async Task A_bundle_is_saved_with_its_slots_and_reads_back_whole()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);
        var combo = Bundle("Латте с круассаном", 28000, Slot(espresso), Slot(croissant));
        await combos.SaveComboAsync(combo);

        var loaded = await combos.GetComboAsync(combo.Id);

        Assert.Equal("Латте с круассаном", loaded!.Name);
        // Блюда подгружены: экран каталога рисует состав по именам, а не по идентификаторам, и слот
        // без Product.Name нарисуется пустым.
        Assert.Collection(
            loaded.Components.OrderBy(component => component.Product?.Name, StringComparer.Ordinal),
            component => Assert.Equal("Круассан", component.Product?.Name),
            component => Assert.Equal("Эспрессо", component.Product?.Name));
    }

    [Fact]
    public async Task Saving_replaces_the_whole_component_set()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var croissant = await DishAsync(catalog, "Круассан", 180m);
        var combo = Bundle("Латте с круассаном", 28000, Slot(espresso), Slot(croissant));
        await combos.SaveComboAsync(combo);

        var loaded = await combos.GetComboAsync(combo.Id);
        var espressoSlot = loaded!.Components.First(component => component.ProductId == espresso.Id);

        // Убрали круассан — и он должен ПРОПАТЬ из каталога. Частичная запись здесь означала бы, что
        // удаление компонента из формы не удаляет его из набора: комбо продолжал бы продавать то,
        // что оператор убрал, а он бы убрал ещё раз и перестал доверять экрану.
        loaded.Name = "Просто латте";
        loaded.Components.RemoveAll(component => component.ProductId == croissant.Id);
        await combos.SaveComboAsync(loaded);

        var reloaded = await combos.GetComboAsync(combo.Id);
        Assert.Equal("Просто латте", reloaded!.Name);
        Assert.Equal([espresso.Id], reloaded.Components.Select(component => component.ProductId));
        // Уцелевший слот сохранил идентификатор — он пережил правку, а не был пересоздан.
        Assert.Equal(espressoSlot.Id, reloaded.Components[0].Id);
    }

    [Fact]
    public async Task A_bundle_with_no_name_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var combos = host.Get<IComboService>();

        await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("   ", 15000, Slot(new Product { Id = Guid.NewGuid(), Name = "X", Price = 1m }))));
    }

    [Fact]
    public async Task A_bundle_with_no_components_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var combos = host.Get<IComboService>();

        // Набор без слотов продавать нечем, даже если у него названа цена.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Пустой", 15000)));

        Assert.Contains("хотя бы один компонент", failure.Message);
    }

    [Fact]
    public async Task A_bundle_price_is_saved_and_reads_back_exactly()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        // Своя цена ПИШЕТСЯ, а не вычисляется: 250 ₽ при сумме слотов 120 ₽ — это наценка, и она должна
        // пережить и сохранение, и повторное чтение из базы без всякого пересчёта.
        var combo = Bundle("Кофе", 25000, Slot(espresso));
        await combos.SaveComboAsync(combo);

        Assert.Equal(25000, (await combos.GetComboAsync(combo.Id))!.PriceKopecks);
    }

    [Fact]
    public async Task Editing_a_bundle_can_change_its_price_without_touching_the_slots()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();

        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        var combo = Bundle("Кофе", 12000, Slot(espresso));
        await combos.SaveComboAsync(combo);

        var loaded = await combos.GetComboAsync(combo.Id);
        loaded!.PriceKopecks = 13500;
        await combos.SaveComboAsync(loaded);

        // Цена изменилась — слот прежний, и идентификатор слота прежний: пересчёт цены не имеет права
        // пересоздавать состав.
        var reloaded = await combos.GetComboAsync(combo.Id);
        Assert.Equal(13500, reloaded!.PriceKopecks);
        Assert.Equal(loaded.Components[0].Id, reloaded.Components[0].Id);
        Assert.Equal(espresso.Id, reloaded.Components[0].ProductId);
    }

    [Fact]
    public async Task A_bundle_with_no_price_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        // Цена 0 ₽ — это не «бесплатный напиток», а недозаполненная карточка: продавать по ней можно
        // было бы, и касса отдала бы набор за ноль, потому что ноль — это разрешённая цена, а не
        // «не задано». Отказ называет причину, а не просто молчит.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Кофе", 0, Slot(espresso))));

        Assert.Contains("цену комбо", failure.Message);
    }

    [Fact]
    public async Task A_bundle_with_a_negative_price_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        // Минус — это не скидка, это ошибка знака: деньги за набор платить не будут.
        await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Кофе", -100, Slot(espresso))));
    }

    [Fact]
    public async Task A_component_below_one_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        // Кратность 0 — это не «бесплатная порция», это слот, который ничего не добавляет к набору и
        // тихо ломает цену. Кратность 0 отдельно от цены 0: у бесплатного компонента кратность 1.
        await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Кофе", 15000, Slot(espresso, quantityPerUnit: 0))));
    }

    [Fact]
    public async Task A_slot_pointing_at_a_deleted_dish_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);
        await catalog.DeleteProductAsync(espresso.Id);

        // Форма могла быть открыта до того, как блюдо удалили. Слот, переживший удаление, — это ровно то
        // состояние, из которого потом пришлось бы отказывать на кассе при внешне нормальном комбо.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Кофе", 15000, Slot(espresso))));

        Assert.Contains("нет в каталоге", failure.Message);
    }

    [Fact]
    public async Task A_substitute_that_does_not_exist_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        var slot = Slot(espresso);
        slot.SubstituteProductId = Guid.NewGuid();
        await Assert.ThrowsAsync<ValidationFailureException>(() => combos.SaveComboAsync(Bundle("Кофе", 15000, slot)));
    }

    [Fact]
    public async Task A_negative_slot_price_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        await Assert.ThrowsAsync<ValidationFailureException>(
            () => combos.SaveComboAsync(Bundle("Кофе", 15000, Slot(espresso, priceKopecks: -1))));
    }

    [Fact]
    public async Task Deleting_a_bundle_is_a_soft_delete()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        var combo = Bundle("Кофе", 15000, Slot(espresso));
        await combos.SaveComboAsync(combo);
        await combos.DeleteComboAsync(combo.Id);

        // Уходит из каталога, но остаётся в истории: проданные наборы печатаются из снимка состава, и
        // жёсткое удаление забрало бы с собой слоты вместе с единственным, по чему их можно прочитать.
        Assert.DoesNotContain(await combos.GetCombosAsync(), current => current.Id == combo.Id);
        var deleted = await combos.GetComboAsync(combo.Id);
        Assert.True(deleted!.IsDeleted);
    }

    [Fact]
    public async Task Bundles_are_listed_by_sort_order_then_name()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        await combos.SaveComboAsync(Bundle("Завтрак", 15000, Slot(espresso)));      // SortOrder 0
        await combos.SaveComboAsync(Bundle("Обед", 15000, Slot(espresso)));         // SortOrder 0
        var late = Bundle("Ужин", 15000, Slot(espresso));
        late.SortOrder = 5;
        await combos.SaveComboAsync(late);

        Assert.Equal(["Завтрак", "Обед", "Ужин"], (await combos.GetCombosAsync()).Select(combo => combo.Name));
    }

    [Fact]
    public async Task Two_bundles_can_share_a_dish()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var combos = host.Get<IComboService>();
        var espresso = await DishAsync(catalog, "Эспрессо", 120m);

        await combos.SaveComboAsync(Bundle("Эспрессо", 15000, Slot(espresso)));
        await combos.SaveComboAsync(Bundle("Двойной эспрессо", 15000, Slot(espresso, quantityPerUnit: 2)));

        // Обход того самого ограничения, что описано в ComboExpander: один ингредиент входит в два
        // набора, и склад спишется по обоим. Формула верна, инвариант «сколько полных наборов можно
        // собрать сейчас» — нет, и это отдельная задача, а не повод запрещать два набора из одного блюда.
        var loaded = await combos.GetCombosAsync();
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, combo => Assert.Single(combo.Components));
    }
}
