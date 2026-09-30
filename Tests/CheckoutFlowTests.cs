using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>Checkout: one transaction for the order + stock write-off, atomic order numbers.</summary>
public class CheckoutFlowTests
{
    private static async Task<(Product Product, Ingredient Ingredient)> SeedCatalogAsync(ICatalogService catalog)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        await catalog.SaveProductAsync(product);

        var ingredient = new Ingredient
        {
            Id = Guid.NewGuid(),
            Name = "Молоко",
            Unit = "мл",
            CostPerUnit = 0.06m,
            StockQuantity = 1000m,
            MinStockLevel = 100m,
            IsAvailable = true
        };
        await catalog.SaveIngredientAsync(ingredient);
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            IngredientId = ingredient.Id,
            Quantity = 200m
        });

        return (product, ingredient);
    }

    [Fact]
    public async Task Checkout_assigns_sequential_order_numbers()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, _) = await SeedCatalogAsync(catalog);

        var first = await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 1)]);
        var second = await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 1)]);

        Assert.Equal(1, first.OrderNumber);
        Assert.Equal(2, second.OrderNumber);
        Assert.Equal(220m, first.TotalPrice);
        Assert.Equal(OrderStatus.InProgress, first.Status);
        Assert.Single(first.Items);
    }

    [Fact]
    public async Task Checkout_writes_off_stock_by_recipe()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, ingredient) = await SeedCatalogAsync(catalog);

        await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 2)]); // 2 × 200 мл

        var updated = await catalog.GetIngredientAsync(ingredient.Id);
        Assert.Equal(600m, updated!.StockQuantity);
    }

    [Fact]
    public async Task Checkout_rejects_an_empty_cart()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();

        await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync([]));
    }

    [Fact]
    public async Task Checkout_fails_when_stock_is_insufficient_and_writes_nothing()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, ingredient) = await SeedCatalogAsync(catalog);

        // Needs 6 × 200 мл = 1200 мл, stock is 1000.
        await Assert.ThrowsAsync<InsufficientStockException>(
            () => checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 6)]));

        var unchanged = await catalog.GetIngredientAsync(ingredient.Id);
        Assert.Equal(1000m, unchanged!.StockQuantity);
        Assert.Empty(await host.Get<IOrderService>().GetActiveOrdersAsync());
    }

    /// <summary>
    /// Regression: a line added to an in-progress order used to fail the same way the product card
    /// did — the entity went into the loaded Items collection, EF tracked it as Modified and issued
    /// an UPDATE for a row that did not exist yet.
    /// </summary>
    [Fact]
    public async Task Adding_a_line_to_an_open_order_saves_it_and_recalculates_the_total()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var (latte, _) = await SeedCatalogAsync(catalog);
        var croissant = new Product { Id = Guid.NewGuid(), Name = "Круассан", Price = 120m, IsAvailable = true };
        await catalog.SaveProductAsync(croissant);

        var order = await host.Get<ICheckoutService>()
            .CheckoutAsync([new CheckoutLine(latte.Id, latte.Name, 220m, 1)]);

        await host.Get<IOrderService>().UpdateOrderAsync(order.Id,
        [
            new OrderItem { Id = Guid.NewGuid(), ProductId = latte.Id, ProductName = latte.Name, Price = 220m, Quantity = 2 },
            new OrderItem { Id = Guid.NewGuid(), ProductId = croissant.Id, ProductName = croissant.Name, Price = 120m, Quantity = 1 }
        ]);

        var updated = await host.Get<IOrderService>().GetOrderAsync(order.Id);

        Assert.Equal(2, updated!.Items.Count);
        Assert.Equal(560m, updated.TotalPrice);
    }
}
