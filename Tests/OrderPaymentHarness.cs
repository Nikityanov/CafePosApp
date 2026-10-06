using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CafePosApp.Tests;

/// <summary>
/// Seeds and helpers shared by the order, payment, refund, stock and shift suites.
/// </summary>
/// <remarks>
/// <b>THIS IS STATIC AND PULLED IN WITH <c>using static</c>, ON PURPOSE.</b> The alternative
/// is a base class, and a base class would give the suites a hierarchy to reason about for no
/// gain: they share fixtures, not behaviour. Splitting one 1 319-line file into six meant
/// moving these helpers out, and <c>using static</c> does that without touching a single call
/// site — which is the only way a move of this size can be checked by a build.
/// </remarks>
internal static class OrderPaymentHarness
{
    public  const decimal LattePrice = 220m;
    public  const long LatteKopecks = 22000;
    public  const decimal MilkPerLatte = 200m;
    public  const decimal MilkStock = 100000m;

    /// <summary>A latte plus the single ingredient its recipe writes off.</summary>
    public  static async Task<(Product Product, Ingredient Milk)> SeedLatteWithMilkAsync(ICatalogService catalog)
    {
        var product = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = LattePrice, IsAvailable = true };
        await catalog.SaveProductAsync(product);

        // A recipe is required: checkout refuses to write off stock it cannot account for.
        var milk = new Ingredient
        {
            Id = Guid.NewGuid(),
            Name = "Молоко",
            Unit = "мл",
            CostPerUnit = 0.06m,
            StockQuantity = MilkStock,
            MinStockLevel = 100m,
            IsAvailable = true
        };
        await catalog.SaveIngredientAsync(milk);
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            IngredientId = milk.Id,
            Quantity = MilkPerLatte
        });

        return (product, milk);
    }

    public  static async Task<Product> SeedLatteAsync(ICatalogService catalog) =>
        (await SeedLatteWithMilkAsync(catalog)).Product;

    public  static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, PaymentIntent? payment = null) =>
        await CheckoutAsync(checkout, product, 1, payment);

    public  static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, int quantity, PaymentIntent? payment = null) =>
        payment is null
            ? await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)])
            : await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)], payment);

    /// <summary>Walks an order all the way to Completed — the only state a refund is allowed in.</summary>
    public  static async Task<Order> CompleteAsync(IOrderService orders, Guid orderId)
    {
        await orders.AdvanceStatusAsync(orderId);
        return await orders.AdvanceStatusAsync(orderId);
    }

    public  static async Task<decimal> StockOfAsync(TestHost.Host host, Guid ingredientId)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Ingredients.AsNoTracking().SingleAsync(row => row.Id == ingredientId)).StockQuantity;
    }
}
