using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// «Дописать»: a phone and a promised time added to an order AFTER it was paid for, and the one rule
/// that has to hold — a phone lives on a takeaway order and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// These assert against the real database, for the reason <see cref="ContactDataTests"/> gives: what
/// matters is what ended up in the row, not what the method returned.
/// </para>
/// <para>
/// <b>THE CONTOUR IS ASSERTED AS A REFUSAL, NOT AS A FLAG.</b> The dangerous outcome here is not a wrong
/// phone, it is a phone sitting on a counter-service order at all — CoAP 13.11 ч.12 counts ROWS. So the
/// negative case is a first-class test rather than an afterthought, and the promotion case asserts the
/// TYPE actually moved, because a phone stored without the order becoming takeaway would be exactly the
/// row the law is about.
/// </para>
/// </remarks>
public class AddContactDetailsTests
{
    private static async Task<Product> LatteAsync(TestHost.Host host)
    {
        var catalog = host.Get<ICatalogService>();
        var latte = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        await catalog.SaveProductAsync(latte);
        return latte;
    }

    private static CheckoutLine Line(Product latte) => new(latte.Id, latte.Name, 220m, 1);

    [Fact]
    public async Task A_phone_on_a_counter_service_order_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)], new OrderDetailsIntent(OrderType.CounterService));

        // No promoteToTakeaway, so the write must be refused rather than quietly storing the number.
        await Assert.ThrowsAsync<ValidationFailureException>(() =>
            orders.AddContactDetailsAsync(order.Id, "9161234567", null));

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Null(reloaded!.CustomerPhone);
        Assert.Equal(OrderType.CounterService, reloaded.OrderType);
    }

    [Fact]
    public async Task Promoting_to_takeaway_stores_the_phone_and_moves_the_order()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)], new OrderDetailsIntent(OrderType.CounterService));

        await orders.AddContactDetailsAsync(order.Id, "8 (916) 123-45-67", null, promoteToTakeaway: true);

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal("+79161234567", reloaded!.CustomerPhone);
        Assert.Equal(OrderType.Takeaway, reloaded.OrderType);
    }

    [Fact]
    public async Task A_phone_on_a_takeaway_order_needs_no_promotion()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)], new OrderDetailsIntent(OrderType.Takeaway));

        await orders.AddContactDetailsAsync(order.Id, "+7 916 123-45-67", null);

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal("+79161234567", reloaded!.CustomerPhone);
        Assert.Equal(OrderType.Takeaway, reloaded.OrderType);
    }

    [Fact]
    public async Task A_promised_time_alone_never_moves_the_fulfilment_type()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)], new OrderDetailsIntent(OrderType.CounterService));

        var promised = DateTimeOffset.Now.AddHours(2);
        await orders.AddContactDetailsAsync(order.Id, null, promised);

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Null(reloaded!.CustomerPhone);
        Assert.Equal(promised, reloaded.RequestedAt);

        // THE POINT OF THE TEST: a promise is not a contact. Recording when an order is wanted says
        // nothing about where the customer is, so the fulfilment type must not follow it.
        Assert.Equal(OrderType.CounterService, reloaded.OrderType);
    }

    [Fact]
    public async Task A_rubbish_phone_is_refused_and_nothing_is_written()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)], new OrderDetailsIntent(OrderType.Takeaway));

        await Assert.ThrowsAsync<ValidationFailureException>(() =>
            orders.AddContactDetailsAsync(order.Id, "не номер", null));

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Null(reloaded!.CustomerPhone);
    }
}
