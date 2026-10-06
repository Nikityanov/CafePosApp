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

/// <summary>The ledger behind Orders.PaidKopecks: every write path has to leave it reconciled.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class OrderLedgerTests
{

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
}
