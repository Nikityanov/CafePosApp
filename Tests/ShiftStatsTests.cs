using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// The shift-level aggregates behind the analytics card.
/// </summary>
/// <remarks>
/// Only the peak-hour line lives here, and only because it was the one count on that card composed by
/// hand instead of through <see cref="TextFormat.Plural"/>. It read «11:00–12:00 (1 заказов)» on a
/// shift with a single order — found by looking at a screenshot of a 360dp run, not by a failing
/// test, because a grammar slip moves no number and asserts no rule that a green run would notice.
/// Every other count on the same card pluralises correctly, which is precisely why this one survived
/// a visual pass: the odd form had to be read, not computed.
/// </remarks>
public class ShiftStatsTests
{
    private const decimal LattePrice = 220m;

    private static async Task<Product> SeedLatteAsync(ICatalogService catalog)
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
            StockQuantity = 100000m,
            MinStockLevel = 100m,
            IsAvailable = true
        };
        await catalog.SaveIngredientAsync(milk);
        await catalog.SaveRecipeItemAsync(new RecipeItem
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            IngredientId = milk.Id,
            Quantity = 200m
        });

        return product;
    }

    private static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product) =>
        await checkout.CheckoutAsync(
            [new CheckoutLine(product.Id, product.Name, LattePrice, 1)],
            new PaymentIntent(LattePrice, PaymentMethod.Cash));

    private static async Task CompleteAsync(IOrderService orders, Guid orderId)
    {
        await orders.AdvanceStatusAsync(orderId);
        await orders.AdvanceStatusAsync(orderId);
    }

    /// <summary>
    /// The Russian plural form agrees with the order count, so the busiest hour of a one-order shift
    /// reads «1 заказ» and not «1 заказов». Three counts, one per form: the interesting boundary
    /// cases (11, 12, 21) belong to <see cref="TextFormat.Plural"/>'s own test, and duplicating them
    /// here would only re-test the helper.
    /// </summary>
    [Theory]
    [InlineData(1, "заказ")]
    [InlineData(2, "заказа")]
    [InlineData(5, "заказов")]
    public async Task The_peak_hour_agrees_with_the_order_count(int orderCount, string expectedForm)
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var checkout = host.Get<ICheckoutService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var shift = await orders.OpenShiftAsync(0);

        // Every order lands in the same wall-clock hour, so the peak hour count is the order count.
        for (var index = 0; index < orderCount; index++)
        {
            var order = await CheckoutAsync(checkout, product);
            await CompleteAsync(orders, order.Id);
        }

        var stats = await orders.GetShiftStatsAsync(shift.Id);

        Assert.Equal(orderCount, stats.CompletedCount);
        Assert.EndsWith($"({orderCount} {expectedForm})", stats.PeakHour);
    }

    /// <summary>
    /// A shift with nothing completed has no busiest hour at all, and says so with a dash rather than
    /// inventing «(0 заказов)». The empty case is where a pluralised builder is most tempted to
    /// produce a count of nothing, so it is worth stating rather than leaving implied.
    /// </summary>
    [Fact]
    public async Task An_empty_shift_has_no_peak_hour()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        var shift = await orders.OpenShiftAsync(0);

        var stats = await orders.GetShiftStatsAsync(shift.Id);

        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal("—", stats.PeakHour);
    }
}
