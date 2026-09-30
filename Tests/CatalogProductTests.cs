using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// Editing a product card. Regression: adding a variant to an already saved product used to fail
/// with "expected to affect 1 row(s), but actually affected 0" — the new variant was pushed into
/// the loaded Variants collection, which EF tracks as Modified (its key is already set), so EF
/// issued an UPDATE for a row that did not exist yet.
/// </summary>
public class CatalogProductTests
{
    private static async Task<ICatalogService> BootAsync(TestHost.Host host)
    {
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        return host.Get<ICatalogService>();
    }

    [Fact]
    public async Task Adding_variants_to_a_saved_product_saves_them()
    {
        using var host = TestHost.Create();
        var catalog = await BootAsync(host);

        var product = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        await catalog.SaveProductAsync(product);

        await catalog.SaveProductAsync(new Product
        {
            Id = product.Id,
            Name = "Латте",
            Price = 0m,
            IsAvailable = true,
            HasVariants = true,
            Variants =
            [
                new ProductVariant { Id = Guid.NewGuid(), Name = "М", Price = 150m, IsAvailable = true, SortOrder = 0 },
                new ProductVariant { Id = Guid.NewGuid(), Name = "С", Price = 200m, IsAvailable = true, SortOrder = 1 }
            ]
        });

        var saved = await catalog.GetProductAsync(product.Id);

        Assert.NotNull(saved);
        Assert.True(saved.HasVariants);
        Assert.Equal(0, saved.PriceKopecks);
        Assert.Equal(["М", "С"], saved.Variants.OrderBy(item => item.SortOrder).Select(item => item.Name));
        Assert.Equal([15000, 20000], saved.Variants.OrderBy(item => item.SortOrder).Select(item => item.PriceKopecks));
    }

    [Fact]
    public async Task Adding_a_variant_to_a_product_that_already_has_variants_keeps_the_old_ones()
    {
        using var host = TestHost.Create();
        var catalog = await BootAsync(host);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Кофе",
            Price = 0m,
            IsAvailable = true,
            HasVariants = true,
            Variants = [new ProductVariant { Id = Guid.NewGuid(), Name = "М", Price = 150m, IsAvailable = true, SortOrder = 0 }]
        };
        await catalog.SaveProductAsync(product);

        var reloaded = await catalog.GetProductAsync(product.Id);
        Assert.NotNull(reloaded);
        var keptId = reloaded.Variants.Single().Id;

        await catalog.SaveProductAsync(new Product
        {
            Id = product.Id,
            Name = "Кофе",
            Price = 0m,
            IsAvailable = true,
            HasVariants = true,
            Variants =
            [
                new ProductVariant { Id = keptId, Name = "М", Price = 160m, IsAvailable = true, SortOrder = 0 },
                new ProductVariant { Id = Guid.NewGuid(), Name = "С", Price = 200m, IsAvailable = true, SortOrder = 1 }
            ]
        });

        var saved = await catalog.GetProductAsync(product.Id);

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Variants.Count);
        Assert.Equal([16000, 20000], saved.Variants.OrderBy(item => item.SortOrder).Select(item => item.PriceKopecks));
    }

    [Fact]
    public async Task Removing_a_variant_deletes_it()
    {
        using var host = TestHost.Create();
        var catalog = await BootAsync(host);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Кофе",
            Price = 0m,
            IsAvailable = true,
            HasVariants = true,
            Variants =
            [
                new ProductVariant { Id = Guid.NewGuid(), Name = "М", Price = 150m, IsAvailable = true, SortOrder = 0 },
                new ProductVariant { Id = Guid.NewGuid(), Name = "С", Price = 200m, IsAvailable = true, SortOrder = 1 }
            ]
        };
        await catalog.SaveProductAsync(product);

        var reloaded = await catalog.GetProductAsync(product.Id);
        Assert.NotNull(reloaded);
        var keptId = reloaded.Variants.Single(item => item.Name == "М").Id;

        await catalog.SaveProductAsync(new Product
        {
            Id = product.Id,
            Name = "Кофе",
            Price = 0m,
            IsAvailable = true,
            HasVariants = true,
            Variants = [new ProductVariant { Id = keptId, Name = "М", Price = 150m, IsAvailable = true, SortOrder = 0 }]
        });

        var saved = await catalog.GetProductAsync(product.Id);

        Assert.NotNull(saved);
        Assert.Equal(["М"], saved.Variants.Select(item => item.Name));
    }
}
