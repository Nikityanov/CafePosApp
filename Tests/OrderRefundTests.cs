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

/// <summary>Refunds: mirrored per payment method, capped at what is left, and only once the order has left the bar.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class OrderRefundTests
{

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;
        await CompleteAsync(orders, order.Id);

        // The count is the mandatory part of a close now: 220 ₽ was taken in cash and the drawer is
        // counted as holding exactly that, so the close needs no reason.
        var next = await orders.CloseShiftAsync(LatteKopecks, null);
        Assert.Equal(shiftId, next.Id);

        await orders.RefundAsync(order.Id, 120m, "вернули после смены");

        // The old shift's drawer line reflects the money that left it. Nothing was opened after the
        // close, so there is no second shift for the refund to have wrongly landed in — and that is
        // exactly what used to be asserted here, against the shift the close used to open itself.
        var closed = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice, closed.PaymentsCash);
        Assert.Equal(120m, closed.RefundsCash);
        Assert.Equal(LattePrice - 120m, closed.PaymentsCash - closed.RefundsCash);
        Assert.Null(await orders.GetActiveShiftAsync());
    }

    /// <summary>A fully refunded order still closes out — refunding does not leave an order open.</summary>
    [Fact]
    public async Task A_fully_refunded_completed_order_does_not_block_closing_the_shift()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        await orders.RefundAsync(order.Id, LattePrice, "вернули всё");

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Completed, reloaded!.Status);
        Assert.Equal(0, reloaded.PaidKopecks);

        var next = await orders.CloseShiftAsync(0, "всё вернули, касса пуста");

        // Closing leaves the terminal with NO open shift, which is a normal state now — it is what
        // the opening screen is for. The point of this test is the close itself: a fully refunded
        // Completed order must not hold it open.
        Assert.False(next.IsActive);
        Assert.Null(await orders.GetActiveShiftAsync());
    }
}
