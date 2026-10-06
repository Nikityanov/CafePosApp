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

/// <summary>Closing a shift: what it reports, what the CSV says, and what a refund does to the drawer line.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class ShiftCloseTests
{

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        await CheckoutAsync(host.Get<ICheckoutService>(), product); // 220 ₽, untouched
        await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));

        // No count is given here and none is needed: the bar still has orders on it, so the close is
        // refused before anything about the cash is even looked at.
        var exception = await Assert.ThrowsAsync<ConflictException>(() => orders.CloseShiftAsync(0, null));

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        await CheckoutAsync(checkout, product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CheckoutAsync(checkout, product, new PaymentIntent(100m, PaymentMethod.Cash));
        await CheckoutAsync(checkout, product, new PaymentIntent(50m, PaymentMethod.Card));
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var checkout = host.Get<ICheckoutService>();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());
        var shiftId = (await orders.GetActiveShiftAsync())!.Id;

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

        // This shift was never closed, so it was never counted. The export has to SAY that in one
        // line: an uncounted shift printed as a drawer of 0.00 would be a fabricated count on
        // exactly the historical rows somebody re-checks first.
        Assert.Contains("Пересчёт;не проводился", csv);
        Assert.DoesNotContain("Ожидалось на момент закрытия", csv);
        Assert.DoesNotContain("Пересчитано в кассе", csv);

        // The per-order block is untouched: it prints status and amount per row, so a voided sale is
        // already visible in the CSV today and did not need a change.
        Assert.Contains("Cancelled", csv);
        Assert.Contains("Completed", csv);
    }

}
