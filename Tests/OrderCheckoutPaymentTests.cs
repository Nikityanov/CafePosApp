using System.Text.RegularExpressions;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Schema.Migrations;
using CafePos.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static CafePosApp.Tests.OrderPaymentHarness;

namespace CafePosApp.Tests;

/// <summary>Checkout: what the payment at the till records, and which transitions it unlocks.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class OrderCheckoutPaymentTests
{

    [Fact]
    public async Task Checkout_with_a_payment_marks_the_order_paid()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));

        Assert.Equal(LatteKopecks, order.TotalKopecks);
        Assert.Equal(LatteKopecks, order.PaidKopecks);
        Assert.Equal(0, order.BalanceKopecks);
        Assert.True(order.IsFullyPaid);
        Assert.Equal(PaymentState.Paid, order.PaymentState);

        var payments = await host.Get<IOrderService>().GetOrderPaymentsAsync(order.Id);
        var payment = Assert.Single(payments);
        Assert.Equal(LatteKopecks, payment.AmountKopecks);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Equal(order.Id, payment.OrderId);

        // And the state survives a fresh read of the order, not just the returned instance.
        var reloaded = await host.Get<IOrderService>().GetOrderAsync(order.Id);
        Assert.Equal(PaymentState.Paid, reloaded!.PaymentState);
    }

    [Fact]
    public async Task Checkout_without_a_payment_defers_it()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);

        Assert.Empty(await host.Get<IOrderService>().GetOrderPaymentsAsync(order.Id));
        Assert.Equal(0, order.PaidKopecks);
        Assert.Equal(LatteKopecks, order.BalanceKopecks);
        Assert.False(order.IsFullyPaid);
        Assert.Equal(PaymentState.Unpaid, order.PaymentState);
    }

    [Fact]
    public async Task Partial_cash_then_the_rest_by_card_ends_paid()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);
        await orders.AddPaymentAsync(order.Id, 100m, PaymentMethod.Cash);
        var partial = await orders.AddPaymentAsync(order.Id, 120m, PaymentMethod.Card);

        Assert.Equal(PaymentState.Paid, partial.PaymentState);
        Assert.Equal(LatteKopecks, partial.PaidKopecks);

        var payments = await orders.GetOrderPaymentsAsync(order.Id);
        Assert.Equal(2, payments.Count);
        Assert.Equal(PaymentMethod.Cash, payments[0].Method);
        Assert.Equal(10000, payments[0].AmountKopecks);
        Assert.Equal(PaymentMethod.Card, payments[1].Method);
        Assert.Equal(12000, payments[1].AmountKopecks);
    }

    [Fact]
    public async Task Cash_tendered_over_the_balance_is_capped_at_the_total()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        // 1000 ₽ note for a 220 ₽ order: 780 ₽ is change and must never reach the ledger.
        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(1000m, PaymentMethod.Cash));

        Assert.Equal(LatteKopecks, order.PaidKopecks);
        Assert.True(order.PaidKopecks <= order.TotalKopecks);
        var payment = Assert.Single(await orders.GetOrderPaymentsAsync(order.Id));
        Assert.Equal(LatteKopecks, payment.AmountKopecks);

        // The same clamp on the later path: a second, larger tendered amount is trimmed to what is
        // actually still owed, not rejected and not taken in full.
        var second = await CheckoutAsync(host.Get<ICheckoutService>(), product);
        await orders.AddPaymentAsync(second.Id, 100m, PaymentMethod.Cash);
        var clamped = await orders.AddPaymentAsync(second.Id, 5000m, PaymentMethod.Card);
        Assert.Equal(LatteKopecks, clamped.PaidKopecks);
        var rows = await orders.GetOrderPaymentsAsync(second.Id);
        Assert.Equal(12000, rows[1].AmountKopecks);
    }

    [Fact]
    public async Task Payment_on_a_cancelled_order_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);
        await orders.CancelOrderAsync(order.Id);

        await Assert.ThrowsAsync<ConflictException>(
            () => orders.AddPaymentAsync(order.Id, LattePrice, PaymentMethod.Cash));
        Assert.Empty(await orders.GetOrderPaymentsAsync(order.Id));
    }

    [Fact]
    public async Task Payment_on_a_fully_paid_order_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));

        await Assert.ThrowsAsync<ConflictException>(
            () => orders.AddPaymentAsync(order.Id, 1m, PaymentMethod.Card));

        // The refused attempt must leave no trace at all.
        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(LatteKopecks, reloaded!.PaidKopecks);
        Assert.Single(await orders.GetOrderPaymentsAsync(order.Id));
    }

    [Fact]
    public async Task Advancing_to_ready_while_unpaid_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);

        await Assert.ThrowsAsync<ConflictException>(() => orders.AdvanceStatusAsync(order.Id));

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.InProgress, reloaded!.Status);
    }

    [Fact]
    public async Task Advancing_while_partially_paid_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);
        await orders.AddPaymentAsync(order.Id, 100m, PaymentMethod.Cash);

        await Assert.ThrowsAsync<ConflictException>(() => orders.AdvanceStatusAsync(order.Id));
    }

    [Fact]
    public async Task Advancing_after_payment_walks_the_order_to_completed()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Card));

        var ready = await orders.AdvanceStatusAsync(order.Id);
        Assert.Equal(OrderStatus.Ready, ready.Status);

        var completed = await orders.AdvanceStatusAsync(order.Id);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(PaymentState.Paid, completed.PaymentState);
    }
}
