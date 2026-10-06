using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>Parked and autosaved carts: a crash must never lose a half-finished order.</summary>
public class DraftOrderTests
{
    private static async Task<CheckoutLine> SeedLineAsync(ICatalogService catalog)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Капучино", Price = 180m, IsAvailable = true };
        await catalog.SaveProductAsync(product);
        return new CheckoutLine(product.Id, product.Name, 180m, 2, "Карамель", "Большой");
    }

    [Fact]
    public async Task Active_cart_survives_a_save_load_cycle()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var line = await SeedLineAsync(host.Get<ICatalogService>());

        await drafts.SaveActiveCartAsync([line]);
        var snapshot = await drafts.LoadActiveCartAsync();

        Assert.False(snapshot.IsEmpty);
        var restored = Assert.Single(snapshot.Lines);
        Assert.Equal(line.ProductId, restored.ProductId);
        Assert.Equal(2, restored.Quantity);
        Assert.Equal("Карамель", restored.ModifierName);
        Assert.Equal("Большой", restored.VariantName);
        Assert.Equal(180m, restored.Price);
    }

    [Fact]
    public async Task A_bundle_survives_a_parking_lot_with_its_slots_intact()
    {
        // THE REGRESSION. Parking a cart dropped a bundle's composition on BOTH sides: the save never
        // wrote the slots, and the load never read them. Nothing failed. A parked bundle came back as a
        // bare line - nothing printed under it, and its merge signature no longer matched the identical
        // bundle the cashier added next, so the cart showed the same bundle twice at the same price
        // instead of one row at quantity 2. Found on an emulator while verifying a different refactor:
        // add a bundle, park the cart, reopen, add the same bundle again, and watch the cart grow.
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var catalog = host.Get<ICatalogService>();

        var bundle = new Product { Id = Guid.NewGuid(), Name = "Комбо", Price = 500m, IsAvailable = true };
        await catalog.SaveProductAsync(bundle);

        var coffeeId = Guid.NewGuid();
        var cakeId = Guid.NewGuid();
        await catalog.SaveProductAsync(new Product { Id = coffeeId, Name = "Кофе", Price = 160m, IsAvailable = true });
        await catalog.SaveProductAsync(new Product { Id = cakeId, Name = "Чизкейк", Price = 280m, IsAvailable = true });

        var parked = new CheckoutLine(
            bundle.Id, bundle.Name, 500m, 1, null, null,
            [
                new CheckoutComponent(coffeeId, "Кофе", 1, 16000, 16000),
                new CheckoutComponent(cakeId, "Чизкейк", 2, 28000, 28000),
            ]);

        await drafts.SaveActiveCartAsync([parked]);
        var restored = Assert.Single((await drafts.LoadActiveCartAsync()).Lines);

        // The identity, so the restored line merges with the next identical bundle instead of
        // standing next to it as a duplicate.
        Assert.NotNull(restored.Components);
        Assert.Equal(2, restored.Components.Count);
        Assert.Equal(
            OrderLineKey.For(bundle.Id, null, null, parked.Components.Select(c => (c.ProductId, c.QuantityPerUnit))),
            OrderLineKey.For(
                restored.ProductId,
                restored.ModifierName,
                restored.VariantName,
                restored.Components!.Select(c => (c.ProductId, c.QuantityPerUnit))));

        // And the figures the kitchen and the reports need, not just the dish names.
        var cake = restored.Components.Single(c => c.ProductId == cakeId);
        Assert.Equal("Чизкейк", cake.ProductName);
        Assert.Equal(2, cake.QuantityPerUnit);
        Assert.Equal(28000, cake.UnitPriceKopecks);
        Assert.Equal(28000, cake.ReferencePriceKopecks);
    }

    [Fact]
    public async Task A_parked_and_taken_bundle_keeps_its_slots_too()
    {
        // Park and Take is the other door out of the parking lot, and it had its own copy of the same
        // omission. A fix applied to one path and not the other is a fix that waits.
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var catalog = host.Get<ICatalogService>();

        var bundle = new Product { Id = Guid.NewGuid(), Name = "Комбо", Price = 500m, IsAvailable = true };
        var coffee = new Product { Id = Guid.NewGuid(), Name = "Кофе", Price = 160m, IsAvailable = true };
        await catalog.SaveProductAsync(bundle);
        await catalog.SaveProductAsync(coffee);

        // ParkAsync, not SaveActiveCartAsync: GetParkedAsync excludes the active cart, so an autosaved
        // cart is not parked and has no id to take.
        var parkedDraft = await drafts.ParkAsync(
            "Утро",
            [new CheckoutLine(
                bundle.Id, bundle.Name, 500m, 1, null, null,
                [new CheckoutComponent(coffee.Id, "Кофе", 1, 16000, 16000)])]);

        Assert.Contains(await drafts.GetParkedAsync(), draft => draft.Id == parkedDraft.Id);

        var taken = Assert.Single((await drafts.TakeAsync(parkedDraft.Id)).Lines);

        Assert.Equal([coffee.Id], taken.Components!.Select(component => component.ProductId));
    }

    [Fact]
    public async Task An_ordinary_dish_still_parks_as_one_line_with_no_slots()
    {
        // The other direction: the fix must not make every parked line look like a bundle.
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var line = await SeedLineAsync(host.Get<ICatalogService>());

        await drafts.SaveActiveCartAsync([line]);
        var restored = Assert.Single((await drafts.LoadActiveCartAsync()).Lines);

        Assert.Empty(restored.Components ?? []);
    }

    [Fact]
    public async Task A_second_save_replaces_the_lines_instead_of_failing()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var catalog = host.Get<ICatalogService>();
        var line = await SeedLineAsync(catalog);

        // The regression this guards: saving TWICE. Every other test in this file saves once,
        // and a single save passes through the path where the draft itself is Added, which
        // cascades Added to its dependents. Only the second save hits an Unchanged draft, and
        // that is where client-assigned Guid keys made EF track the replacement lines as
        // Modified, producing DELETE-then-UPDATE and DbUpdateConcurrencyException. The
        // operator saw "Не удалось добавить блюдо" and the cart was never persisted, so a
        // killed app lost the order.
        await drafts.SaveActiveCartAsync([line]);

        var second = new Product { Id = Guid.NewGuid(), Name = "Чай", Price = 90m, IsAvailable = true };
        await catalog.SaveProductAsync(second);
        var secondLine = new CheckoutLine(second.Id, second.Name, 90m, 1, null, null);

        await drafts.SaveActiveCartAsync([secondLine]);

        var snapshot = await drafts.LoadActiveCartAsync();
        var restored = Assert.Single(snapshot.Lines);
        Assert.Equal(second.Id, restored.ProductId);
        Assert.Equal(90m, restored.Price);

        // The replaced line must be gone, not merely shadowed: a stale line surviving here
        // would silently reappear in an order the operator thought they had changed.
        Assert.DoesNotContain(snapshot.Lines, l => l.ProductId == line.ProductId);
    }

    [Fact]
    public async Task Clear_removes_the_active_cart()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var line = await SeedLineAsync(host.Get<ICatalogService>());

        await drafts.SaveActiveCartAsync([line]);
        await drafts.ClearActiveCartAsync();

        Assert.True((await drafts.LoadActiveCartAsync()).IsEmpty);
    }

    [Fact]
    public async Task Park_and_take_roundtrip()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var drafts = host.Get<IDraftOrderService>();
        var line = await SeedLineAsync(host.Get<ICatalogService>());

        var parked = await drafts.ParkAsync("Стол 5", [line]);

        var parkedList = await drafts.GetParkedAsync();
        Assert.Single(parkedList);
        Assert.Equal("Стол 5", parkedList[0].Name);
        Assert.False(parkedList[0].IsActiveCart);

        var taken = await drafts.TakeAsync(parked.Id);
        Assert.Single(taken.Lines);
        Assert.Empty(await drafts.GetParkedAsync());
    }
}
