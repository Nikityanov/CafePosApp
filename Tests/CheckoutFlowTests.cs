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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());

        await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync([]));
    }

    [Fact]
    public async Task Checkout_fails_when_stock_is_insufficient_and_writes_nothing()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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

    /// <summary>
    /// The rule the whole till-owns-the-money feature rests on: with no shift open there is no float,
    /// so a sale has nothing to be counted against and must not happen. Every other checkout test
    /// opens a shift first and so cannot notice if this guard is ever removed — which is exactly how a
    /// guard that all the tests pass without ever exercising disappears.
    /// </summary>
    [Fact]
    public async Task Checkout_is_refused_while_no_shift_is_open()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, _) = await SeedCatalogAsync(catalog);

        // No TestHost.OpenEmptyShiftAsync — that is the whole setup.
        var failure = await Assert.ThrowsAsync<ConflictException>(
            () => checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 1)]));

        // The refusal has to name the way out, not just the problem: the operator is standing at a
        // terminal that refuses to sell, and "смена не открыта" alone does not tell them what to do.
        Assert.Contains("Откройте смену", failure.Message);
        Assert.Contains("внесите размен", failure.Message);
    }

    /// <summary>
    /// A refused checkout must leave nothing behind. The order is written in a transaction that the
    /// refusal aborts, so the trap here is a half-written order that shows up on the board with a
    /// number consumed — the shift's numbering would then start at 2 for the first real sale.
    /// </summary>
    [Fact]
    public async Task A_refused_checkout_leaves_no_order_and_consumes_no_order_number()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, ingredient) = await SeedCatalogAsync(catalog);

        await Assert.ThrowsAsync<ConflictException>(
            () => checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 1)]));

        Assert.Empty(await orders.GetActiveOrdersAsync());

        // The stock write-off belongs to the same transaction, so a refusal must not have spent
        // 200 мл of milk on an order that does not exist. 1000 is the seeded amount; without this
        // the test would still pass if the refusal rolled back the order but not the ingredients.
        Assert.Equal(1000m, (await catalog.GetIngredientAsync(ingredient.Id))!.StockQuantity);

        // And the numbering still starts at 1 once the shift is opened — the proof that the aborted
        // attempt did not reserve one.
        await TestHost.OpenEmptyShiftAsync(orders);
        var first = await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, 220m, 1)]);

        Assert.Equal(1, first.OrderNumber);
        Assert.Equal(800m, (await catalog.GetIngredientAsync(ingredient.Id))!.StockQuantity);
    }
}
