using System.Text.RegularExpressions;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Schema.Migrations;
using CafePos.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CafePosApp.Tests;

/// <summary>
/// Payments: the ledger behind <c>Orders.PaidKopecks</c>, the cap at the balance, the status
/// block, refunds (mirrored per method, capped, Completed-only), cancellation that voids a paid
/// sale, the stock reversal that undoes a write-off, and the migrations that backfill the column
/// and the ledger of every order that existed before the feature.
/// </summary>
public class OrderPaymentTests
{
    private const decimal LattePrice = 220m;
    private const long LatteKopecks = 22000;
    private const decimal MilkPerLatte = 200m;
    private const decimal MilkStock = 100000m;

    /// <summary>A latte plus the single ingredient its recipe writes off.</summary>
    private static async Task<(Product Product, Ingredient Milk)> SeedLatteWithMilkAsync(ICatalogService catalog)
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

    private static async Task<Product> SeedLatteAsync(ICatalogService catalog) =>
        (await SeedLatteWithMilkAsync(catalog)).Product;

    private static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, PaymentIntent? payment = null) =>
        await CheckoutAsync(checkout, product, 1, payment);

    private static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, int quantity, PaymentIntent? payment = null) =>
        payment is null
            ? await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)])
            : await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)], payment);

    /// <summary>Walks an order all the way to Completed — the only state a refund is allowed in.</summary>
    private static async Task<Order> CompleteAsync(IOrderService orders, Guid orderId)
    {
        await orders.AdvanceStatusAsync(orderId);
        return await orders.AdvanceStatusAsync(orderId);
    }

    private static async Task<decimal> StockOfAsync(TestHost.Host host, Guid ingredientId)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return (await db.Ingredients.AsNoTracking().SingleAsync(row => row.Id == ingredientId)).StockQuantity;
    }

    [Fact]
    public async Task Checkout_with_a_payment_marks_the_order_paid()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
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
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Card));

        var ready = await orders.AdvanceStatusAsync(order.Id);
        Assert.Equal(OrderStatus.Ready, ready.Status);

        var completed = await orders.AdvanceStatusAsync(order.Id);
        Assert.Equal(OrderStatus.Completed, completed.Status);
        Assert.Equal(PaymentState.Paid, completed.PaymentState);
    }

    /// <summary>
    /// The invariant every screen and every report leans on, checked on all seven write paths at
    /// once (checkout with payment, deferred checkout, partial payments, over-tendered cash, a
    /// cancelled unpaid order, a cancelled paid order, a partial refund): the scalar is the
    /// ledger's SIGNED SUM, it never exceeds the order total, and it is never negative.
    /// <para>
    /// The direction lives in <c>IsRefund</c>, so the invariant reads
    /// <c>SUM(Where(!IsRefund)) − SUM(Where(IsRefund))</c>. Three bounds are asserted, not one,
    /// because each catches a different break: the two-sided equality catches a writer that touched
    /// the rows or the scalar but not both, <c>&lt;= TotalKopecks</c> catches a refund booked with
    /// the wrong direction (a refund only ever lowers it), and <c>&gt;= 0</c> catches the cap in
    /// <c>PaymentRecorder.Refund</c> being removed — without it every screen would read a negative
    /// debt as an amount still to collect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_write_path_leaves_the_ledger_reconciled()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var paid = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        var deferred = await CheckoutAsync(checkout, product);
        var partial = await CheckoutAsync(checkout, product);
        await orders.AddPaymentAsync(partial.Id, 50m, PaymentMethod.Cash);
        var overpaid = await CheckoutAsync(checkout, product, new PaymentIntent(5000m, PaymentMethod.Cash));
        var cancelled = await CheckoutAsync(checkout, product);
        await orders.CancelOrderAsync(cancelled.Id);

        // A cancelled order that WAS paid: this used to be refused outright, and the refund is what
        // takes the money back out again.
        var voided = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await orders.CancelOrderAsync(voided.Id);

        // And a partial refund on a finished order, so the ledger holds both directions at once.
        var refunded = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, refunded.Id);
        await orders.RefundAsync(refunded.Id, 20m, "разбилось");

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var expected = new Dictionary<Guid, long>
        {
            [paid.Id] = LatteKopecks,
            [deferred.Id] = 0,
            [partial.Id] = 5000,
            [overpaid.Id] = LatteKopecks,
            [cancelled.Id] = 0,
            [voided.Id] = 0,
            [refunded.Id] = LatteKopecks - 2000
        };

        foreach (var order in await db.Orders.ToListAsync())
        {
            var rows = await db.OrderPayments
                .Where(payment => payment.OrderId == order.Id)
                .Select(payment => new { payment.IsRefund, payment.AmountKopecks })
                .ToListAsync();
            var collected = rows.Where(row => !row.IsRefund).Sum(row => row.AmountKopecks);
            var returned = rows.Where(row => row.IsRefund).Sum(row => row.AmountKopecks);

            Assert.Equal(expected[order.Id], order.PaidKopecks);
            Assert.Equal(order.PaidKopecks, collected - returned);
            Assert.True(order.PaidKopecks <= order.TotalKopecks, $"Order {order.OrderNumber} recorded more than it is worth.");
            Assert.True(order.PaidKopecks >= 0, $"Order {order.OrderNumber} recorded a negative amount received.");
            // Every amount in the ledger is positive; the direction is the flag, never the sign.
            Assert.DoesNotContain(rows, row => row.AmountKopecks <= 0);
        }
    }

    /// <summary>
    /// The order lists load orders with <c>AsNoTracking</c> and no <c>Include</c> of the payments,
    /// and EF only links tracked entities into a collection during DetectChanges — so a
    /// [NotMapped] state derived from <c>order.Payments</c> would read zero there and every card
    /// would claim "unpaid" with no error anywhere. This test is the one that keeps
    /// <see cref="Order.PaidKopecks"/> from being swapped for a collection sum.
    /// </summary>
    [Fact]
    public async Task Active_orders_report_the_payment_state_without_loading_payments()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var paid = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        var deferred = await CheckoutAsync(checkout, product);
        var partial = await CheckoutAsync(checkout, product);
        await orders.AddPaymentAsync(partial.Id, 100m, PaymentMethod.Cash);

        var active = await orders.GetActiveOrdersAsync();

        var paidRow = active.Single(order => order.Id == paid.Id);
        var deferredRow = active.Single(order => order.Id == deferred.Id);
        var partialRow = active.Single(order => order.Id == partial.Id);

        Assert.Equal(PaymentState.Paid, paidRow.PaymentState);
        Assert.Equal(PaymentState.Unpaid, deferredRow.PaymentState);
        Assert.Equal(PaymentState.PartiallyPaid, partialRow.PaymentState);
        Assert.Equal(10000, partialRow.PaidKopecks);
        Assert.Equal(12000, partialRow.BalanceKopecks);

        // The collection really is empty here — this is why the domain may not read it.
        Assert.Empty(paidRow.Payments);
    }

    /// <summary>
    /// The inverse of what this test used to assert. It was "cancelling a paid order is refused",
    /// which was correct only while there was no way to give the money back — it left a real
    /// cashier with a customer holding goods and cash and no button that would settle it. Now a paid
    /// order cancels: the sale leaves the revenue, the drawer loses exactly what it took (one refund
    /// row per original payment, under the same methods, so no till is credited or debited money it
    /// never handled), and the order ends up holding nothing.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_paid_order_voids_the_sale_and_refunds_it_in_full()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;

        // 100 ₽ cash + 120 ₽ card: two tills, so the refund has to mirror both.
        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product);
        await orders.AddPaymentAsync(order.Id, 100m, PaymentMethod.Cash);
        await orders.AddPaymentAsync(order.Id, 120m, PaymentMethod.Card);
        await CompleteAsync(orders, order.Id);

        Assert.Equal(LattePrice, (await orders.GetShiftStatsAsync(shiftId)).Revenue);

        await orders.CancelOrderAsync(order.Id, "гость передумал");

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Cancelled, reloaded!.Status);
        Assert.Equal(0, reloaded.PaidKopecks);

        var ledger = await orders.GetOrderPaymentsAsync(order.Id);
        var refunds = ledger.Where(row => row.IsRefund).ToList();
        Assert.Equal(2, refunds.Count);
        // The set, not the sequence: the two rows are written in one operation and therefore share a
        // timestamp, so the read order between them is not defined. WHICH amount lands on which till
        // is the invariant that matters, and it is exactly what these two assertions pin.
        Assert.Equal(10000, refunds.Single(row => row.Method == PaymentMethod.Cash).AmountKopecks);
        Assert.Equal(12000, refunds.Single(row => row.Method == PaymentMethod.Card).AmountKopecks);

        // The refund carries the reason, so the ledger says WHY the money left without the order
        // having to be correlated with a UI field.
        Assert.All(refunds, row => Assert.Contains("Отмена заказа", row.Note));

        // The sale is out of the day's revenue, while the money is out of the drawer. Gross is NOT
        // lowered by the refund: what the till took is still 220, and the money that left is additive.
        var stats = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(0m, stats.Revenue);
        Assert.Equal(100m, stats.PaymentsCash);
        Assert.Equal(120m, stats.PaymentsCard);
        Assert.Equal(100m, stats.RefundsCash);
        Assert.Equal(120m, stats.RefundsCard);
        Assert.Equal(LattePrice, stats.RefundsTotal);

        // And it is still in the history, marked as voided — the thing a manager needs to see.
        var history = await orders.GetShiftOrderHistoryAsync(shiftId);
        Assert.Equal(OrderStatus.Cancelled, Assert.Single(history).Status);

        // Single-shot: a second tap must not refund the same money again.
        await Assert.ThrowsAsync<ConflictException>(() => orders.CancelOrderAsync(order.Id));
    }

    /// <summary>
    /// The status guard on a refund is load-bearing, not a nicety: a refund lowers PaidKopecks, and
    /// a Ready order's total is FROZEN, so allowing one would produce an order that can no longer
    /// become fully paid and can never be advanced — a sale stuck at the bar with money in it. The
    /// operator's move there is to cancel the order, which voids the sale and refunds in one step.
    /// </summary>
    [Fact]
    public async Task Refunding_an_order_that_has_not_left_the_bar_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var ready = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await orders.AdvanceStatusAsync(ready.Id);

        await Assert.ThrowsAsync<ConflictException>(() => orders.RefundAsync(ready.Id, 220m, "передумал"));

        // Refused with no trace: the order is untouched and still fully paid, so it can still close.
        var reloaded = await orders.GetOrderAsync(ready.Id);
        Assert.Equal(OrderStatus.Ready, reloaded!.Status);
        Assert.Equal(LatteKopecks, reloaded.PaidKopecks);
        Assert.Single(await orders.GetOrderPaymentsAsync(ready.Id));
    }

    /// <summary>
    /// The whole point of refusing a method on a refund: a POS has two tills. 200 ₽ arrived in the
    /// drawer and 220 ₽ on the terminal, so a 420 ₽ refund has to leave BOTH — booked as cash it
    /// would leave the drawer 220 ₽ short at the count and credit the terminal money it never paid
    /// out, with nothing in the app to notice. A partial refund splits the same way, oldest payment
    /// first, which also gives refunded(method) &lt;= collected(method) for free.
    /// </summary>
    [Fact]
    public async Task A_refund_mirrors_the_methods_the_money_arrived_in()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        // 440 ₽ collected 200 cash + 240 card, handed over.
        var full = await CheckoutAsync(checkout, product, 2);
        await orders.AddPaymentAsync(full.Id, 200m, PaymentMethod.Cash);
        await orders.AddPaymentAsync(full.Id, 240m, PaymentMethod.Card);
        await CompleteAsync(orders, full.Id);

        await orders.RefundAsync(full.Id, 440m, "вернули всё");

        var refunds = (await orders.GetOrderPaymentsAsync(full.Id)).Where(row => row.IsRefund).ToList();
        Assert.Equal(2, refunds.Count);
        Assert.Equal(20000, refunds.Single(row => row.Method == PaymentMethod.Cash).AmountKopecks);
        Assert.Equal(24000, refunds.Single(row => row.Method == PaymentMethod.Card).AmountKopecks);
        Assert.Equal(0, (await orders.GetOrderAsync(full.Id))!.PaidKopecks);

        // A partial one splits across the same sources: the whole cash payment first, then the card
        // for the remainder.
        var part = await CheckoutAsync(checkout, product, 2);
        await orders.AddPaymentAsync(part.Id, 200m, PaymentMethod.Cash);
        await orders.AddPaymentAsync(part.Id, 240m, PaymentMethod.Card);
        await CompleteAsync(orders, part.Id);

        await orders.RefundAsync(part.Id, 250m, "часть");

        var slices = (await orders.GetOrderPaymentsAsync(part.Id)).Where(row => row.IsRefund).ToList();
        Assert.Equal(2, slices.Count);
        Assert.Equal(20000, slices.Single(row => row.Method == PaymentMethod.Cash).AmountKopecks);
        Assert.Equal(5000, slices.Single(row => row.Method == PaymentMethod.Card).AmountKopecks);
        Assert.Equal(44000 - 25000, (await orders.GetOrderAsync(part.Id))!.PaidKopecks);

        // And a SECOND refund on the same order stays inside the original payments: asking for
        // 190 more takes the last of the card, it does not bill the drawer for a second 200 of cash
        // it never received. Capped per method, so the drawer is never debited more than it took.
        await orders.RefundAsync(part.Id, 190m, "ещё");

        var all = (await orders.GetOrderPaymentsAsync(part.Id)).Where(row => row.IsRefund).ToList();
        var cashBack = all.Where(row => row.Method == PaymentMethod.Cash).Sum(row => row.AmountKopecks);
        var cardBack = all.Where(row => row.Method == PaymentMethod.Card).Sum(row => row.AmountKopecks);
        Assert.Equal(20000, cashBack);
        Assert.Equal(24000, cardBack);
        Assert.Equal(44000, cashBack + cardBack);
        Assert.Equal(0, (await orders.GetOrderAsync(part.Id))!.PaidKopecks);
    }

    /// <summary>
    /// A partial refund returns part of the money for a sale that DID happen, so the order stays
    /// Completed and stays in the revenue — voiding the whole sale is what cancelling is for. Only
    /// the amount the order still holds changes.
    /// </summary>
    [Fact]
    public async Task A_partially_refunded_order_is_still_completed_and_still_in_revenue()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        var refunded = await orders.RefundAsync(order.Id, 70m, "не тот напиток");

        Assert.Equal(OrderStatus.Completed, refunded.Status);
        Assert.Equal(LatteKopecks - 7000, refunded.PaidKopecks);
        Assert.Equal(7000, refunded.BalanceKopecks);
        Assert.Equal(PaymentState.PartiallyPaid, refunded.PaymentState);
        Assert.Equal("не тот напиток", (await orders.GetOrderPaymentsAsync(order.Id)).Single(row => row.IsRefund).Note);

        var stats = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(LattePrice, stats.Revenue);
        // Gross is untouched: what the till took is still 220, and the refund is additive.
        Assert.Equal(LattePrice, stats.PaymentsCash);
        Assert.Equal(70m, stats.RefundsCash);
    }

    /// <summary>
    /// A cashier who types a bigger number than the order is worth must not be able to drive the
    /// scalar negative: a negative PaidKopecks would read as a debt larger than the order, break the
    /// &lt;= TotalKopecks reconciliation, and make a finished sale look like it still owes money.
    /// </summary>
    [Fact]
    public async Task A_refund_larger_than_the_order_is_worth_is_capped()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        var refunded = await orders.RefundAsync(order.Id, 5000m, "перепутал");

        Assert.Equal(0, refunded.PaidKopecks);
        Assert.Equal(LatteKopecks, (await orders.GetOrderPaymentsAsync(order.Id)).Single(row => row.IsRefund).AmountKopecks);
        Assert.True(refunded.PaidKopecks >= 0);

        // And nothing left to give back: the second attempt is refused rather than booking a zero row.
        await Assert.ThrowsAsync<ConflictException>(() => orders.RefundAsync(order.Id, 10m, "ещё раз"));
    }

    /// <summary>
    /// A refund against an already CLOSED shift is allowed and changes that shift's report, because
    /// GetShiftStatsAsync recomputes live off the ledger with no cache. The money physically left
    /// the drawer that shift owned; booking it into the next shift instead would be the lie, since
    /// the new drawer never held the cash. This test is the written form of that decision — and the
    /// companion assertion is that a fully refunded order must not block the shift close.
    /// </summary>
    [Fact]
    public async Task A_refund_against_a_closed_shift_changes_that_shifts_report()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;
        await CompleteAsync(orders, order.Id);

        var next = await orders.CloseShiftAsync();
        Assert.NotEqual(shiftId, next.Id);

        await orders.RefundAsync(order.Id, 120m, "вернули после смены");

        // The old shift's drawer line reflects the money that left it; the new shift never saw it.
        var closed = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice, closed.PaymentsCash);
        Assert.Equal(120m, closed.RefundsCash);
        Assert.Equal(LattePrice - 120m, closed.PaymentsCash - closed.RefundsCash);
        Assert.Equal(0m, (await orders.GetShiftStatsAsync(next.Id)).RefundsCash);
    }

    /// <summary>A fully refunded order still closes out — refunding does not leave an order open.</summary>
    [Fact]
    public async Task A_fully_refunded_completed_order_does_not_block_closing_the_shift()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        await orders.RefundAsync(order.Id, LattePrice, "вернули всё");

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Completed, reloaded!.Status);
        Assert.Equal(0, reloaded.PaidKopecks);

        var next = await orders.CloseShiftAsync();

        Assert.True(next.IsActive);
    }

    /// <summary>
    /// The operator's one decision on a cancellation: nothing was made, so the write-off is undone.
    /// The journal is read and inverted — the ingredient is back at its pre-checkout level, and the
    /// reversal itself is a journal row, so the shelf's history shows the give-back.
    /// </summary>
    [Fact]
    public async Task Cancelling_with_the_stock_returned_puts_the_ingredients_back()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, milk) = await SeedLatteWithMilkAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product, 2);
        Assert.Equal(MilkStock - 400m, await StockOfAsync(host, milk.Id));

        await orders.CancelOrderAsync(order.Id, stock: StockDisposition.ReturnToStock);

        Assert.Equal(MilkStock, await StockOfAsync(host, milk.Id));

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var reversal = await db.StockMovements.AsNoTracking().SingleAsync(row => row.Reason.StartsWith("Возврат по заказу"));
        Assert.Equal(400m, reversal.QuantityDelta);
        Assert.Equal(MilkStock, reversal.StockAfter);
    }

    /// <summary>
    /// The default, and the answer for goods that were made, handed over, or simply do not match the
    /// shelf: the ingredients stay written off. Nothing here moves the stock, so the journal keeps
    /// saying the bar consumed them.
    /// </summary>
    [Fact]
    public async Task Cancelling_with_the_stock_written_off_leaves_the_ingredients_alone()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, milk) = await SeedLatteWithMilkAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product);
        await orders.CancelOrderAsync(order.Id);

        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.DoesNotContain(await db.StockMovements.AsNoTracking().ToListAsync(),
            row => row.Reason.StartsWith("Возврат по заказу"));
    }

    /// <summary>
    /// The composed reason has to fit both columns it is written to — Orders.CancellationReason and
    /// OrderStatusHistory.Comment are 300 characters each — and a careless operator typing a novel
    /// must not be able to push the refund or the stock disposition out of the audit trail. The tail
    /// that goes is the operator's free text, which is the only part of it that can afford it.
    /// </summary>
    [Fact]
    public async Task A_long_cancellation_reason_is_truncated_to_the_column_limit()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await orders.CancelOrderAsync(order.Id, new string('я', 500), StockDisposition.ReturnToStock);

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.NotNull(reloaded!.CancellationReason);
        Assert.Equal(300, reloaded.CancellationReason.Length);
        Assert.True(reloaded.PaidKopecks == 0);

        var history = await orders.GetStatusHistoryAsync(order.Id);
        var comment = Assert.Single(history, row => row.Status == OrderStatus.Cancelled).Comment;
        Assert.NotNull(comment);
        Assert.Equal(300, comment.Length);
    }

    /// <summary>
    /// WHY the reversal reads the journal instead of re-running the recipe: a write-off is a recorded
    /// event, and SaveRecipeItemAsync edits recipe quantities with no versioning, so the recipe at
    /// cancel time is not the recipe at checkout. A recompute would return 500 ml where 200 ml left
    /// the shelf and invent 300 ml of milk that was never there.
    /// </summary>
    [Fact]
    public async Task The_stock_reversal_returns_what_was_written_off_not_what_the_recipe_says_now()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, milk) = await SeedLatteWithMilkAsync(catalog);

        var order = await CheckoutAsync(checkout, product);
        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));

        // The bar fixes the recipe AFTER the sale: the next latte now takes 500 ml.
        var recipeItem = (await catalog.GetRecipeItemsByProductAsync(product.Id)).Single();
        recipeItem.Quantity = 500m;
        await catalog.SaveRecipeItemAsync(recipeItem);

        await orders.CancelOrderAsync(order.Id, stock: StockDisposition.ReturnToStock);

        // Back to the pre-checkout level — 200 ml returned, not 500.
        Assert.Equal(MilkStock, await StockOfAsync(host, milk.Id));
    }

    /// <summary>
    /// The belt-and-braces guard before the reversal negates anything. Nothing in the app writes a
    /// POSITIVE movement with an OrderId today — a delivery has no order, RestockAsync leaves
    /// OrderId null, and a write-off is clamped to zero rather than flipped — but a future
    /// per-order manual adjustment would carry the same OrderId, and blind negation would silently
    /// undo that too. The whole reversal is refused rather than half-applied, and because the
    /// cancellation is one transaction the refusal also leaves the refund and the status untouched.
    /// </summary>
    [Fact]
    public async Task Cancelling_refuses_to_return_stock_when_the_order_has_a_receipt_in_the_journal()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, milk) = await SeedLatteWithMilkAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        // A delivery booked against this order — impossible through the app, so written by hand.
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.StockMovements.Add(new StockMovement
            {
                IngredientId = milk.Id,
                QuantityDelta = 500m,
                StockAfter = MilkStock + 500m,
                Reason = "Поставка по заказу",
                OrderId = order.Id,
                CreatedAt = DateTimeOffset.Parse("2026-09-21T09:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture)
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(
            () => orders.CancelOrderAsync(order.Id, stock: StockDisposition.ReturnToStock));

        // Nothing was written: the sale is intact, the money is still on the order and the stock
        // level is what it was.
        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Completed, reloaded!.Status);
        Assert.Equal(LatteKopecks, reloaded.PaidKopecks);
        Assert.DoesNotContain(await orders.GetOrderPaymentsAsync(order.Id), row => row.IsRefund);
        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));

        // And the operator still has the way out: the goods were made, so leave the stock written off.
        await orders.CancelOrderAsync(order.Id, stock: StockDisposition.LeaveWrittenOff);
        Assert.Equal(OrderStatus.Cancelled, (await orders.GetOrderAsync(order.Id))!.Status);
    }

    /// <summary>
    /// A journal row whose ingredient is no longer in the catalogue cannot be inverted: there is no
    /// stock level to put anything back into. That must be REPORTED, not skipped — the operator was
    /// told the goods came back, and a silent skip is a lie about the shelf with nothing on screen to
    /// contradict it. The report reaches Orders.CancellationReason and the status history.
    /// </summary>
    [Fact]
    public async Task A_journal_row_whose_ingredient_is_gone_is_reported_not_skipped()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var (product, milk) = await SeedLatteWithMilkAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product);
        Assert.Equal(MilkStock - MilkPerLatte, await StockOfAsync(host, milk.Id));

        // The shape a database that had foreign keys OFF (before SQLite enforcement was on, or a
        // hand-edited file) leaves behind: the ingredient row is gone but its journal survives,
        // orphaned. DeleteIngredientAsync now refuses to create this state — see the next test — so
        // it has to be built by hand to be covered.
        var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF";
            await pragma.ExecuteNonQueryAsync();
        }
        await using (var command = connection.CreateCommand())
        {
            // UPPER-case "D" format, the way the provider writes a Guid key. A lower-case literal
            // silently matches nothing here (SQLite compares TEXT case-sensitively), the row survives
            // the DELETE, and the test below would pass for the wrong reason.
            command.CommandText = $"DELETE FROM [Ingredients] WHERE [Id] = '{milk.Id.ToString("D").ToUpperInvariant()}'";
            var deleted = await command.ExecuteNonQueryAsync();
            Assert.Equal(1, deleted);
        }
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            await pragma.ExecuteNonQueryAsync();
        }
        await connection.DisposeAsync();

        await orders.CancelOrderAsync(order.Id, stock: StockDisposition.ReturnToStock);

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Cancelled, reloaded!.Status);
        // The order is still voided and the money still went back — only the stock is partial.
        Assert.Equal(0, reloaded.PaidKopecks);
        Assert.NotNull(reloaded.CancellationReason);
        Assert.Contains("вернуто частично", reloaded.CancellationReason);

        var history = await orders.GetStatusHistoryAsync(order.Id);
        Assert.Contains(history, row => row.Comment != null && row.Comment.Contains("вернуто частично"));
    }

    /// <summary>
    /// The guard that keeps the branch above unreachable from the app. StockMovements.IngredientId
    /// is ON DELETE CASCADE and ingredients are a HARD delete (no IsDeleted flag, unlike products),
    /// so deleting one destroys its journal — there would be nothing left to invert and no name left
    /// to report, only an identifier. Refusing the delete is the only place the truth can be kept:
    /// switching the ingredient off instead keeps the journal intact.
    /// </summary>
    [Fact]
    public async Task An_ingredient_with_stock_history_cannot_be_deleted()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, milk) = await SeedLatteWithMilkAsync(catalog);

        await CheckoutAsync(checkout, product);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => catalog.DeleteIngredientAsync(milk.Id));

        Assert.Contains("Молоко", exception.Message);
        Assert.NotNull(await catalog.GetIngredientAsync(milk.Id));
    }

    /// <summary>
    /// Cancelling flips a Completed order to Cancelled, which would drop it out of
    /// GetCompletedOrdersAsync — so a manager would watch a voided sale vanish from the history
    /// instead of seeing it marked voided. The history query returns both closed statuses.
    /// </summary>
    [Fact]
    public async Task The_shift_history_keeps_a_voided_sale_visible()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;

        var kept = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, kept.Id);
        var voided = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, voided.Id);
        await orders.CancelOrderAsync(voided.Id, "ошибочно пробили");

        // The Completed-only query is left alone — revenue is not what it is for.
        Assert.Equal(OrderStatus.Completed, (await orders.GetCompletedOrdersAsync(shiftId)).Single().Status);

        var history = await orders.GetShiftOrderHistoryAsync(shiftId);
        Assert.Equal(2, history.Count);
        Assert.Equal(OrderStatus.Cancelled, history.Single(row => row.Id == voided.Id).Status);
        Assert.Equal(OrderStatus.Completed, history.Single(row => row.Id == kept.Id).Status);
    }

    [Fact]
    public async Task Closing_a_shift_names_the_uncollected_total()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        await CheckoutAsync(host.Get<ICheckoutService>(), product); // 220 ₽, untouched
        await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => orders.CloseShiftAsync());

        Assert.Contains("незакрытых заказов — 2", exception.Message);
        Assert.Contains("не оплачено 1", exception.Message);
        // 220 ₽ of cash is still on the bar; the manager has to be told how much, not just how many.
        Assert.Matches(new Regex(@"220[.,]00\s*₽"), exception.Message);
    }

    [Fact]
    public async Task Shift_stats_and_the_csv_split_the_payments_by_method()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CheckoutAsync(checkout, product, new PaymentIntent(100m, PaymentMethod.Cash));
        await CheckoutAsync(checkout, product, new PaymentIntent(50m, PaymentMethod.Card));
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;

        var stats = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(3, stats.PaymentsCount);
        Assert.Equal(320m, stats.PaymentsCash);
        Assert.Equal(50m, stats.PaymentsCard);
        Assert.Equal(370m, stats.PaymentsTotal);
        // Revenue is still the sum of the order totals, not the sum of the payments: nothing here
        // has been completed, so the two are not the same figure.
        Assert.Equal(0m, stats.Revenue);

        var csv = await host.Get<IReportExportService>().ExportShiftReportCsvAsync(shiftId);
        Assert.Contains("Принято оплат", csv);
        Assert.Contains("Принято наличными", csv);

        // Nothing has been given back yet: the drawer line equals what was taken in cash.
        Assert.Equal(0m, stats.RefundsCash);
        Assert.Equal(0m, stats.RefundsCard);
        Assert.Equal(0m, stats.RefundsTotal);
        Assert.Contains("Итого наличными в кассе;320.00", csv);
    }

    /// <summary>
    /// Gross stays gross. After a refund the four Payments* fields still say what the till TOOK — the
    /// same numbers as before the refund — and the refunds sit on top of them, so a day with refunds
    /// can never be mistaken for a day that took less. The drawer line is the only net figure, and
    /// it is the one a manager physically counts.
    /// </summary>
    [Fact]
    public async Task The_gross_payments_are_untouched_by_a_refund_and_the_drawer_line_nets_out()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetOrCreateActiveShiftAsync()).Id;

        // Fully cash, finished: 220 in the drawer, later 70 of it handed back.
        var cash = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, cash.Id);
        await orders.RefundAsync(cash.Id, 70m, "не тот напиток");

        // 20 cash + 200 card, finished, then voided: the refund mirrors both tills.
        var mixed = await CheckoutAsync(checkout, product);
        await orders.AddPaymentAsync(mixed.Id, 20m, PaymentMethod.Cash);
        await orders.AddPaymentAsync(mixed.Id, 200m, PaymentMethod.Card);
        await CompleteAsync(orders, mixed.Id);
        await orders.CancelOrderAsync(mixed.Id, "ошибочно пробили");

        var stats = await orders.GetShiftStatsAsync(shiftId);
        // Gross: 220 + 20 cash and 200 card — untouched by either the refund or the cancellation.
        Assert.Equal(3, stats.PaymentsCount);
        Assert.Equal(240m, stats.PaymentsCash);
        Assert.Equal(200m, stats.PaymentsCard);
        Assert.Equal(440m, stats.PaymentsTotal);

        // Refunds: 70 cash on the partial refund, plus 20 cash + 200 card mirrored off the void.
        Assert.Equal(90m, stats.RefundsCash);
        Assert.Equal(200m, stats.RefundsCard);
        Assert.Equal(290m, stats.RefundsTotal);
        Assert.Equal(240m - 90m, stats.PaymentsCash - stats.RefundsCash);

        var csv = await host.Get<IReportExportService>().ExportShiftReportCsvAsync(shiftId);
        Assert.Contains("Возвращено наличными;90.00", csv);
        Assert.Contains("Возвращено картой;200.00", csv);
        Assert.Contains("Возвращено всего;290.00", csv);
        Assert.Contains("Итого наличными в кассе;150.00", csv);

        // The per-order block is untouched: it prints status and amount per row, so a voided sale is
        // already visible in the CSV today and did not need a change.
        Assert.Contains("Cancelled", csv);
        Assert.Contains("Completed", csv);
    }

    /// <summary>
    /// Builds the database the upgrade path actually meets: the first release of the schema (where
    /// Status is still an INTEGER column), migrations 1–5 applied on top, and the given orders
    /// already in the table.
    /// <para>
    /// The identifiers are written the way the provider writes them — UPPER-case "D" format — so
    /// the rows behave like real ones. A hand-written lower-case id is invisible to any
    /// parameterised EF query against the table, which is the same class of mistake as writing
    /// money in the wrong storage format: the row is there and nothing finds it.
    /// </para>
    /// </summary>
    private static async Task BuildV5DatabaseAsync(TestHost.Host host, (Guid Id, long TotalKopecks, string Status)[] orders)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        var shiftId = Guid.NewGuid().ToString().ToUpperInvariant();
        await using var db = await factory.CreateDbContextAsync();
        await SqliteSchemaHelper.ExecuteAsync(db, SchemaMigrationTests.LegacyV1Schema, CancellationToken.None);
        foreach (var migration in SchemaMigrator.AllMigrations.Where(migration => migration.Version <= 5))
            await migration.ApplyAsync(db, CancellationToken.None);

        await SqliteSchemaHelper.ExecuteAsync(db,
            $"INSERT INTO [Shifts] ([Id], [StartTime], [IsActive], [NextOrderNumber]) VALUES ('{shiftId}', '2026-09-20 08:00:00.0000000+00:00', 1, 1)",
            CancellationToken.None);

        for (var index = 0; index < orders.Length; index++)
        {
            var order = orders[index];
            var orderKey = order.Id.ToString("D").ToUpperInvariant();
            await SqliteSchemaHelper.ExecuteAsync(db, $"""
                INSERT INTO [Orders] ([Id], [CreatedAt], [TotalKopecks], [Status], [ShiftId], [OrderNumber])
                    VALUES ('{orderKey}', '2026-09-20 09:{index:00}:00.0000000+00:00', {order.TotalKopecks}, {order.Status}, '{shiftId}', {index + 1})
                """, CancellationToken.None);
        }
    }

    /// <summary>
    /// The upgrade path that matters: a database that already had five migrations and real orders
    /// in it. Existing orders were paid at checkout (payment deferral did not exist before this
    /// feature), so the column is backfilled AND each backfilled order gets a matching synthetic
    /// Cash row — one without the other fabricates either debt or unrecorded cash.
    /// </summary>
    [Fact]
    public async Task Migration_006_backfills_a_populated_v5_database()
    {
        using var host = TestHost.Create();
        var openOrderId = Guid.NewGuid();
        var cancelledOrderId = Guid.NewGuid();
        var legacyCancelledOrderId = Guid.NewGuid();
        await BuildV5DatabaseAsync(host,
        [
            (openOrderId, 50000, "'Ready'"),
            (cancelledOrderId, 30000, "'Cancelled'"),
            // The legacy enum ordinal of Cancelled, in a column that still has INTEGER affinity.
            (legacyCancelledOrderId, 20000, "3")
        ]);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);
            // Version 7 as well: the point of reading the result back through EF is to prove the
            // upgraded database is usable by the current model, and a v6-only schema is not what any
            // device actually runs.
            await new Migration007_Refunds().ApplyAsync(db, CancellationToken.None);
        }

        // Reading through EF, not through raw SQL: this is the shape the app reads afterwards, and
        // it is what would fail first if the synthetic key were not in the provider's own format.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var orders = await db.Orders.AsNoTracking().ToListAsync();
            Assert.Equal(50000, orders.Single(order => order.Id == openOrderId).PaidKopecks);
            Assert.Equal(0, orders.Single(order => order.Id == cancelledOrderId).PaidKopecks);
            Assert.Equal(0, orders.Single(order => order.Id == legacyCancelledOrderId).PaidKopecks);

            var payment = await db.OrderPayments.AsNoTracking().SingleAsync(row => row.OrderId == openOrderId);
            Assert.Equal(50000, payment.AmountKopecks);
            Assert.Equal(PaymentMethod.Cash, payment.Method);
            // The synthetic key has to survive being read back as a Guid, and it must be its own
            // key rather than the order's identifier.
            Assert.NotEqual(Guid.Empty, payment.Id);
            Assert.NotEqual(openOrderId, payment.Id);
            Assert.Equal(0, await db.OrderPayments.CountAsync(row =>
                row.OrderId == cancelledOrderId || row.OrderId == legacyCancelledOrderId));
        }

        // The column is there, so a fresh insert after the upgrade is not rejected for a missing one.
        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info([Orders])";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            Assert.Contains("PaidKopecks", columns);
        }
    }

    /// <summary>
    /// A backfill that under-fires is the worst outcome of this migration: the open orders that
    /// existed before the feature were paid at checkout, and leaving them at 0 would turn them into
    /// debtors — refused at Ready, blocking the shift close, for money that was never owed. This
    /// order's status was written as text ('Completed') into the legacy INTEGER column, which is
    /// what a device upgraded from the first release actually contains.
    /// </summary>
    [Fact]
    public async Task Backfill_recognises_text_statuses_in_a_legacy_integer_column()
    {
        using var host = TestHost.Create();
        var orderId = Guid.NewGuid();
        await BuildV5DatabaseAsync(host, [(orderId, 50000, "'Completed'")]);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);

        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(50000, (await db.Orders.AsNoTracking().SingleAsync()).PaidKopecks);
    }

    /// <summary>Re-running the migration must not duplicate a synthetic payment.</summary>
    [Fact]
    public async Task Migration_006_is_idempotent()
    {
        using var host = TestHost.Create();
        var orderId = Guid.NewGuid();
        await BuildV5DatabaseAsync(host, [(orderId, 50000, "'Completed'")]);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var migration = new Migration006_OrderPayments();
            await migration.ApplyAsync(db, CancellationToken.None);
            await migration.ApplyAsync(db, CancellationToken.None);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(50000, (await db.Orders.AsNoTracking().SingleAsync()).PaidKopecks);
            Assert.Equal(1, await db.OrderPayments.CountAsync());
        }
    }

    /// <summary>
    /// The upgrade path for version 7: a database that already has the payment ledger and real rows
    /// in it. Two columns, both defaulted, and NO backfill — unlike version 6, every row that exists
    /// is a collection, so <c>IsRefund = 0</c> is already the truth and a second pass would only be
    /// a full-table write on a phone's flash.
    /// </summary>
    [Fact]
    public async Task Migration_007_adds_the_refund_columns_to_a_populated_v6_database()
    {
        using var host = TestHost.Create();
        var orderId = Guid.NewGuid();
        await BuildV5DatabaseAsync(host, [(orderId, 50000, "'Completed'")]);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);

        await using (var db = await factory.CreateDbContextAsync())
            await new Migration007_Refunds().ApplyAsync(db, CancellationToken.None);

        // Reading through EF, not raw SQL: the existing row must come back with the new column
        // defaulted, which is what decides whether a pre-upgrade order counts as a collection or as
        // 200 ₽ of unexplained money going the wrong way.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var payment = await db.OrderPayments.AsNoTracking().SingleAsync();
            Assert.False(payment.IsRefund);
            Assert.Null(payment.Note);
            Assert.Equal(50000, payment.AmountKopecks);
        }

        // And a refund row written after the upgrade lands in a database that already has the rows.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var order = await db.Orders.FirstAsync();
            order.Status = OrderStatus.Completed;
            db.OrderPayments.Add(new OrderPayment
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                AmountKopecks = 5000,
                Method = PaymentMethod.Cash,
                PaidAt = DateTimeOffset.Parse("2026-09-21T09:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
                IsRefund = true,
                Note = "вернули"
            });
            await db.SaveChangesAsync();
        }

        await using (var connection = new SqliteConnection($"Data Source={host.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info([OrderPayments])";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            Assert.Contains("IsRefund", columns);
            Assert.Contains("Note", columns);
        }
    }

    /// <summary>Both statements are guarded, so a re-run after a partial failure is a no-op.</summary>
    [Fact]
    public async Task Migration_007_is_idempotent()
    {
        using var host = TestHost.Create();
        var orderId = Guid.NewGuid();
        await BuildV5DatabaseAsync(host, [(orderId, 50000, "'Completed'")]);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);
            var migration = new Migration007_Refunds();
            await migration.ApplyAsync(db, CancellationToken.None);
            await migration.ApplyAsync(db, CancellationToken.None);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.OrderPayments.CountAsync());
            Assert.False((await db.OrderPayments.AsNoTracking().SingleAsync()).IsRefund);
        }
    }
}
