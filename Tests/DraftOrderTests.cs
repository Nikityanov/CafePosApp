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
