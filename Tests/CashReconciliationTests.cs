using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Schema.Migrations;
using CafePos.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace CafePosApp.Tests;

/// <summary>/// <summary>The end-of-shift cash count: what the drawer was expected to hold, what was counted, thedifference and its reason, and the two facts that...</summary>
/// <summary>/// <remarks>Почему так - docs/decisions/</remarks>difference and its reason, and the two facts that are easy to break — a...</summary>
/// <remarks>Why so - docs/decisions/</remarks>
/// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

public class CashReconciliationTests
{
    private const decimal LattePrice = 220m;
    private const long LatteKopecks = 22000;
    private const decimal MilkPerLatte = 200m;
    private const decimal MilkStock = 100000m;
    private const string MissingMoneyReason = "касса пуста, деньги не выдавали";

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

        return product;
    }

    private static Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, PaymentIntent? payment = null) =>
        CheckoutAsync(checkout, product, 1, payment);

    private static Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, int quantity, PaymentIntent? payment = null) =>
        payment is null
            ? checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)])
            : checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, quantity)], payment);

    /// <summary>Walks an order all the way to Completed — the only state a refund is allowed in.</summary>
    private static async Task<Order> CompleteAsync(IOrderService orders, Guid orderId)
    {
        await orders.AdvanceStatusAsync(orderId);
        return await orders.AdvanceStatusAsync(orderId);
    }

    private static async Task<Guid> ActiveShiftAsync(IOrderService orders) => (await orders.GetActiveShiftAsync())!.Id;

    /// <summary>The stored row itself, so the test can tell a stored 0 from an absent count.</summary>
    private static async Task<Shift> ShiftRowAsync(TestHost.Host host, Guid shiftId)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Shifts.AsNoTracking().SingleAsync(shift => shift.Id == shiftId);
    }

    private static async Task<CashReconciliation> ReconciliationAsync(IOrderService orders, Guid shiftId)
    {
        var stats = await orders.GetShiftStatsAsync(shiftId);
        Assert.NotNull(stats.Reconciliation);
        return stats.Reconciliation!;
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task A_count_of_zero_closes_the_shift_and_is_stored_as_a_real_count()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        // A close returns the shift it closed, and opens nothing.
        var closed = await orders.CloseShiftAsync(0, MissingMoneyReason);

        Assert.Equal(shiftId, closed.Id);
        Assert.False(closed.IsActive);
        Assert.Null(await orders.GetActiveShiftAsync());

        var row = await ShiftRowAsync(host, shiftId);
        Assert.True(row.CountedCashKopecks.HasValue, "Пересчёт 0 должен сохраняться как пересчёт, а не как его отсутствие.");
        Assert.Equal(0, row.CountedCashKopecks!.Value);
        Assert.Equal(LatteKopecks, row.ExpectedCashKopecks);
        Assert.Equal(MissingMoneyReason, row.CashDiscrepancyReason);
        Assert.NotNull(row.ReconciledAt);
        Assert.False(row.IsActive);

        var reconciliation = await ReconciliationAsync(orders, shiftId);
        Assert.Equal(0, reconciliation.CountedKopecks);
        Assert.Equal(LatteKopecks, reconciliation.ExpectedKopecks);
        Assert.Equal(-LatteKopecks, reconciliation.DiscrepancyKopecks);
        Assert.Equal(CashDifference.Shortage, reconciliation.Difference);
        Assert.Equal(MissingMoneyReason, reconciliation.Reason);
        Assert.Equal(row.ReconciledAt, reconciliation.CountedAt);
        // "Recorded at", not "counted at" — the column is the audit moment and is written with the
        // close, so it equals EndTime. A small lie about the three minutes of counting that happened
        // first, kept deliberately, and nothing derives anything from it.
        Assert.Equal(row.EndTime, row.ReconciledAt);
    }

    /// <summary>A count of zero is accepted as a count; a count BELOW zero is not a count at all and is refused, which leaves the shift open and every column untouched.</summary>

    [Fact]
    public async Task A_negative_count_is_refused_and_leaves_the_shift_open()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        await Assert.ThrowsAsync<ValidationFailureException>(() => orders.CloseShiftAsync(-1, "опечатка"));

        var row = await ShiftRowAsync(host, shiftId);
        Assert.True(row.IsActive);
        Assert.Null(row.CountedCashKopecks);
        Assert.Null(row.ExpectedCashKopecks);
        Assert.Null(row.ReconciledAt);
        Assert.Null(row.EndTime);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task A_mismatch_without_a_reason_is_refused_and_writes_nothing()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        var exception = await Assert.ThrowsAsync<ValidationFailureException>(() => orders.CloseShiftAsync(0, null));
        Assert.Contains("Укажите причину", exception.Message);
        await Assert.ThrowsAsync<ValidationFailureException>(() => orders.CloseShiftAsync(0, "   "));

        var row = await ShiftRowAsync(host, shiftId);
        Assert.True(row.IsActive);
        Assert.Null(row.CountedCashKopecks);
        Assert.Null(row.ExpectedCashKopecks);
        Assert.Null(row.CashDiscrepancyReason);
        Assert.Null((await orders.GetShiftStatsAsync(shiftId)).Reconciliation);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task A_matching_count_needs_no_reason_and_accepts_one()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        var second = await orders.CloseShiftAsync(LatteKopecks, null);

        var first = await ReconciliationAsync(orders, shiftId);
        Assert.Equal(CashDifference.Matched, first.Difference);
        Assert.Equal(0, first.DiscrepancyKopecks);
        Assert.Null(first.Reason);

        // Nothing opens the next shift any more, so the empty one is opened on purpose. Its empty
        // drawer is a MATCH at a count of 0, and a reason offered anyway is stored rather than
        // refused.
        var empty = await orders.OpenShiftAsync(0);
        var third = await orders.CloseShiftAsync(0, "проверен дважды, касса пуста");

        var secondRow = await ShiftRowAsync(host, empty.Id);
        Assert.Equal(0, secondRow.CountedCashKopecks);
        Assert.Equal("проверен дважды, касса пуста", secondRow.CashDiscrepancyReason);
        Assert.Equal(CashDifference.Matched, (await ReconciliationAsync(orders, empty.Id)).Difference);
        Assert.False(third.IsActive);

        // Closing again did not touch the first shift's reconciliation: a recorded count stands.
        var firstRow = await ShiftRowAsync(host, shiftId);
        Assert.Equal(LatteKopecks, firstRow.CountedCashKopecks);
        Assert.Equal(LatteKopecks, firstRow.ExpectedCashKopecks);
        Assert.Null(firstRow.CashDiscrepancyReason);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task An_over_long_reason_is_refused_rather_than_truncated()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        var exception = await Assert.ThrowsAsync<ValidationFailureException>(
            () => orders.CloseShiftAsync(0, new string('я', 301)));
        Assert.Contains("300", exception.Message);

        // Refused with no trace: the shift is still open and nothing was written.
        var refused = await ShiftRowAsync(host, shiftId);
        Assert.True(refused.IsActive);
        Assert.Null(refused.CountedCashKopecks);

        // The boundary itself is accepted, stored whole.
        var reason = new string('я', 300);
        await orders.CloseShiftAsync(0, reason);

        Assert.Equal(reason, (await ShiftRowAsync(host, shiftId)).CashDiscrepancyReason);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task The_open_order_guard_runs_before_the_reconciliation_checks()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        // Paid but still InProgress: the shift cannot close, and 220 ₽ is in the drawer so a count
        // of 0 would ALSO be a mismatch with no reason given — two different refusals, and only the
        // first one is the truth here.
        await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        var shiftId = await ActiveShiftAsync(orders);

        await Assert.ThrowsAsync<ConflictException>(() => orders.CloseShiftAsync(0, null));
        await Assert.ThrowsAsync<ConflictException>(() => orders.CloseShiftAsync(-100, null));
        await Assert.ThrowsAsync<ConflictException>(() => orders.CloseShiftAsync(0, new string('я', 400)));

        var row = await ShiftRowAsync(host, shiftId);
        Assert.True(row.IsActive);
        Assert.Null(row.CountedCashKopecks);
        Assert.Null((await orders.GetShiftStatsAsync(shiftId)).Reconciliation);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task The_frozen_snapshot_survives_a_refund_taken_after_the_shift_closed()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        // Counted 20 short, and the reason is on record.
        var closed = await orders.CloseShiftAsync(LatteKopecks - 2000, "не выдали сдачу");

        var atClose = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice, atClose.ExpectedCashNow);
        Assert.Equal(-2000, atClose.Reconciliation!.DiscrepancyKopecks);
        Assert.False(atClose.IsReconciliationStale);

        await orders.RefundAsync(order.Id, 120m, "вернули после смены");

        var after = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice - 120m, after.ExpectedCashNow);
        // The snapshot is untouched — including its discrepancy, which is still the difference
        // between the count and the expectation AT THE MOMENT OF THE CLOSE.
        Assert.Equal(LatteKopecks, after.Reconciliation!.ExpectedKopecks);
        Assert.Equal(LatteKopecks - 2000, after.Reconciliation.CountedKopecks);
        Assert.Equal(-2000, after.Reconciliation.DiscrepancyKopecks);
        Assert.Equal(CashDifference.Shortage, after.Reconciliation.Difference);
        Assert.Equal("не выдали сдачу", after.Reconciliation.Reason);
        Assert.True(after.IsReconciliationStale);

        // And the shift opened afterwards never saw that money — its drawer never held it. The close no
        // longer opens one, so it is opened here, which is the whole point: a drawer that cannot be
        // attributed to the shift that owned the cash cannot be reconciled against the wrong one.
        var fresh = await orders.OpenShiftAsync(0);
        Assert.NotEqual(shiftId, fresh.Id);
        Assert.Equal(0m, (await orders.GetShiftStatsAsync(fresh.Id)).ExpectedCashNow);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task A_late_card_refund_moves_neither_figure_and_a_cash_refund_only_the_live_one()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var cash = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, cash.Id);
        var card = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Card));
        await CompleteAsync(orders, card.Id);
        var shiftId = await ActiveShiftAsync(orders);

        // Counted exactly: 220 in cash, nothing else in the drawer.
        await orders.CloseShiftAsync(LatteKopecks, null);

        await orders.RefundAsync(card.Id, 50m, "вернули на карте");

        var afterCard = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice, afterCard.ExpectedCashNow);
        Assert.Equal(LatteKopecks, afterCard.Reconciliation!.ExpectedKopecks);
        Assert.Equal(CashDifference.Matched, afterCard.Reconciliation.Difference);
        Assert.False(afterCard.IsReconciliationStale);

        await orders.RefundAsync(cash.Id, 50m, "вернули наличными");

        var afterCash = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice - 50m, afterCash.ExpectedCashNow);
        Assert.Equal(LatteKopecks, afterCash.Reconciliation!.ExpectedKopecks);
        Assert.Equal(0, afterCash.Reconciliation.DiscrepancyKopecks);
        Assert.True(afterCash.IsReconciliationStale);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task A_fully_refunded_shift_reconciles_against_a_non_negative_expectation()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        await orders.RefundAsync(order.Id, LattePrice, "вернули всё");
        var shiftId = await ActiveShiftAsync(orders);

        Assert.Equal(0m, (await orders.GetShiftStatsAsync(shiftId)).ExpectedCashNow);

        await orders.CloseShiftAsync(0, null);

        var reconciliation = await ReconciliationAsync(orders, shiftId);
        Assert.Equal(0, reconciliation.ExpectedKopecks);
        Assert.Equal(0, reconciliation.CountedKopecks);
        Assert.Equal(0, reconciliation.DiscrepancyKopecks);
        Assert.Equal(CashDifference.Matched, reconciliation.Difference);
        Assert.True((await orders.GetShiftStatsAsync(shiftId)).ExpectedCashNow >= 0);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task The_drawer_figure_stays_non_negative_across_interleaved_partial_refunds()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var first = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, first.Id);
        var second = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Card));
        await CompleteAsync(orders, second.Id);
        var third = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, third.Id);
        var shiftId = await ActiveShiftAsync(orders);

        Assert.Equal(2 * LattePrice, (await orders.GetShiftStatsAsync(shiftId)).ExpectedCashNow);

        var cashRefunded = 0m;
        foreach (var (order, amount) in new[] { (first, 30m), (second, 40m), (third, 20m) })
        {
            await orders.RefundAsync(order.Id, amount, "частично");
            if (order.Id != second.Id) cashRefunded += amount;

            var live = (await orders.GetShiftStatsAsync(shiftId)).ExpectedCashNow;
            Assert.True(live >= 0, $"Ожидаемая сумма в кассе ушла в минус: {live}");
            Assert.Equal(2 * LattePrice - cashRefunded, live);
        }

        // Give one order back in full, so the interleaved partials finish the shift's cash at a
        // number no single order could have produced on its own.
        await orders.RefundAsync(first.Id, LattePrice - 30m, "вернули остаток");

        var emptied = await orders.GetShiftStatsAsync(shiftId);
        Assert.Equal(LattePrice - 20m, emptied.ExpectedCashNow);
        Assert.True(emptied.ExpectedCashNow >= 0);

        await orders.CloseShiftAsync(Money.ToKopecks(emptied.ExpectedCashNow), null);

        var reconciliation = await ReconciliationAsync(orders, shiftId);
        Assert.Equal(reconciliation.ExpectedKopecks, reconciliation.CountedKopecks);
        Assert.Equal(CashDifference.Matched, reconciliation.Difference);
    }

    /// <summary>The shift the close opens has never been counted, and "never counted" must stay readable as its own state: null, not a zero that would read as "the drawer came out empty".</summary>

    [Fact]
    public async Task The_newly_opened_shift_has_no_reconciliation()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        var stats = await orders.GetShiftStatsAsync(shiftId);
        Assert.Null(stats.Reconciliation);
        // An uncounted shift is not "stale" - there is nothing to have drifted away from.
        Assert.False(stats.IsReconciliationStale);
        Assert.Equal(LattePrice, stats.ExpectedCashNow);

        var row = await ShiftRowAsync(host, shiftId);
        Assert.Null(row.CountedCashKopecks);
        Assert.Null(row.ExpectedCashKopecks);
        Assert.Null(row.ReconciledAt);
        Assert.Null(row.CashDiscrepancyReason);

        // And closing writes them, which is the other half: NULL means "never counted" only until
        // somebody stands at the till.
        var closed = await orders.CloseShiftAsync(0, MissingMoneyReason);
        var closedRow = await ShiftRowAsync(host, closed.Id);
        Assert.NotNull(closedRow.CountedCashKopecks);
        Assert.NotNull(closedRow.ExpectedCashKopecks);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task The_shift_csv_prints_the_reconciliation_block_with_its_moment()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, 20, new PaymentIntent(4400m, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);

        // Counted 240 short of the 4400 the ledger says is in the drawer.
        await orders.CloseShiftAsync(440000 - 24000, "не выдана сдача");
        await orders.RefundAsync(order.Id, 120m, "вернули после пересчёта");

        var csv = await host.Get<IReportExportService>().ExportShiftReportCsvAsync(shiftId);

        Assert.Contains("Пересчёт кассы (", csv);
        Assert.Contains("Ожидалось на момент закрытия;4400.00", csv);
        Assert.Contains("Пересчитано в кассе;4160.00", csv);
        Assert.Contains("Не хватает;240.00", csv);
        Assert.Contains("Причина;не выдана сдача", csv);
        Assert.Contains("Учтено возвратов после пересчёта;120.00", csv);
        Assert.Contains("Ожидается сейчас;4280.00", csv);
        // Never a bare "Ожидалось" — the moment is what makes the figure checkable.
        Assert.DoesNotContain("Ожидалось;", csv);
        // And the block comes after the drawer line it answers.
        Assert.True(csv.IndexOf("Итого наличными в кассе", StringComparison.Ordinal)
            < csv.IndexOf("Пересчёт кассы (", StringComparison.Ordinal));
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task The_csv_names_an_overage_and_prints_no_difference_line_for_a_match()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await TestHost.OpenEmptyShiftAsync(orders);
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);
        var shiftId = await ActiveShiftAsync(orders);
        await orders.CloseShiftAsync(LatteKopecks + 5000, "сдача из чужой кассы");

        var overage = await host.Get<IReportExportService>().ExportShiftReportCsvAsync(shiftId);
        Assert.Contains("Ожидалось на момент закрытия;220.00", overage);
        Assert.Contains("Пересчитано в кассе;270.00", overage);
        Assert.Contains("Излишек;50.00", overage);
        Assert.DoesNotContain("Не хватает", overage);
        // No money moved after the count, so there is nothing to report after it.
        Assert.DoesNotContain("Учтено возвратов после пересчёта", overage);
        Assert.DoesNotContain("Ожидается сейчас", overage);

        // A close no longer opens the next shift, so the empty shift this case needs is opened on
        // purpose — which is the whole behavioural change: "no shift" is now a state a terminal is
        // in between shifts, and one it has to leave deliberately.
        await TestHost.OpenEmptyShiftAsync(orders);
        await orders.CloseShiftAsync(0, null);

        var emptyShift = await orders.GetLatestShiftAsync();
        var match = await host.Get<IReportExportService>().ExportShiftReportCsvAsync(emptyShift!.Id);
        Assert.Contains("Ожидалось на момент закрытия;0.00", match);
        Assert.Contains("Пересчитано в кассе;0.00", match);
        Assert.DoesNotContain("Не хватает", match);
        Assert.DoesNotContain("Излишек", match);
    }

    /// <summary>THE most important single case: an empty drawer against a full expectation.</summary>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    [Fact]
    public async Task Migration_008_adds_the_reconciliation_columns_to_a_populated_v7_database()
    {
        using var host = TestHost.Create();
        var shiftId = await BuildV7DatabaseAsync(host);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
            await new Migration008_CashReconciliation().ApplyAsync(db, CancellationToken.None);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var shift = await db.Shifts.AsNoTracking().SingleAsync();
            Assert.Equal(shiftId, shift.Id);
            Assert.Null(shift.CountedCashKopecks);
            Assert.Null(shift.ExpectedCashKopecks);
            Assert.Null(shift.ReconciledAt);
            Assert.Null(shift.CashDiscrepancyReason);
        }

        // Through the domain, which is the shape the app reads: the drawer figure is real, and the
        // reconciliation is absent rather than zero.
        var stats = await host.Get<IOrderService>().GetShiftStatsAsync(shiftId);
        Assert.Equal(500m, stats.ExpectedCashNow);
        Assert.Null(stats.Reconciliation);
        Assert.False(stats.IsReconciliationStale);

        // The columns are there to be written, not merely to exist: a count written into the
        // upgraded database reads back whole.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var shift = await db.Shifts.FirstAsync();
            shift.CountedCashKopecks = 48000;
            shift.ExpectedCashKopecks = 50000;
            shift.ReconciledAt = DateTimeOffset.Parse("2026-09-21T20:04:00+00:00", CultureInfo.InvariantCulture);
            shift.CashDiscrepancyReason = "не хватает";
            await db.SaveChangesAsync();
        }

        var written = await host.Get<IOrderService>().GetShiftStatsAsync(shiftId);
        Assert.Equal(CashDifference.Shortage, written.Reconciliation!.Difference);
        Assert.Equal(-2000, written.Reconciliation.DiscrepancyKopecks);
        Assert.Equal("не хватает", written.Reconciliation.Reason);

        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        var columns = await ReadColumnsAsync(connection, "Shifts");
        Assert.Contains("CountedCashKopecks", columns);
        Assert.Contains("ExpectedCashKopecks", columns);
        Assert.Contains("ReconciledAt", columns);
        Assert.Contains("CashDiscrepancyReason", columns);
    }

    /// <summary>All four statements are guarded, so a re-run after a partial failure is a no-op.</summary>
    [Fact]
    public async Task Migration_008_is_idempotent()
    {
        using var host = TestHost.Create();
        var shiftId = await BuildV7DatabaseAsync(host);

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var migration = new Migration008_CashReconciliation();
            await migration.ApplyAsync(db, CancellationToken.None);
            await migration.ApplyAsync(db, CancellationToken.None);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.Shifts.CountAsync());
            var shift = await db.Shifts.AsNoTracking().SingleAsync();
            Assert.Null(shift.CountedCashKopecks);
        }

        // A duplicated ADD COLUMN would be rejected by SQLite, but the guard is what makes a re-run
        // after a half-applied migration a no-op rather than a crash on the next app start.
        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        var columns = await ReadColumnsAsync(connection, "Shifts");
        Assert.Equal(1, columns.Count(name => name == "CountedCashKopecks"));
        Assert.Equal(1, columns.Count(name => name == "ExpectedCashKopecks"));
        Assert.Equal(1, columns.Count(name => name == "ReconciledAt"));
        Assert.Equal(1, columns.Count(name => name == "CashDiscrepancyReason"));

        // And the domain still reads the upgraded shift as never counted after the re-run.
        Assert.Null((await host.Get<IOrderService>().GetShiftStatsAsync(shiftId)).Reconciliation);
    }

    /// <summary>    /// <summary>The database the version 8 upgrade actually meets: the first release of the schema, migrations1–5 on top, then the ledger migrations,...</summary>
    /// <summary>/// <remarks>Почему так - docs/decisions/</remarks>1–5 on top, then the ledger migrations, with a shift and a paid o...</summary>
    /// <remarks>Why so - docs/decisions/</remarks>
    /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

    private static async Task<Guid> BuildV7DatabaseAsync(TestHost.Host host)
    {
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        var shiftId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync();

        await SqliteSchemaHelper.ExecuteAsync(db, SchemaMigrationTests.LegacyV1Schema, CancellationToken.None);
        foreach (var migration in SchemaMigrator.AllMigrations.Where(migration => migration.Version <= 5))
            await migration.ApplyAsync(db, CancellationToken.None);

        await SqliteSchemaHelper.ExecuteAsync(db,
            $"INSERT INTO [Shifts] ([Id], [StartTime], [IsActive], [NextOrderNumber]) VALUES ('{shiftId.ToString("D").ToUpperInvariant()}', '2026-09-20 08:00:00.0000000+00:00', 0, 1)",
            CancellationToken.None);
        await SqliteSchemaHelper.ExecuteAsync(db,
            $"INSERT INTO [Orders] ([Id], [CreatedAt], [TotalKopecks], [Status], [ShiftId], [OrderNumber]) " +
            $"VALUES ('{orderId.ToString("D").ToUpperInvariant()}', '2026-09-20 09:00:00.0000000+00:00', 50000, 'Completed', '{shiftId.ToString("D").ToUpperInvariant()}', 1)",
            CancellationToken.None);

        await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);
        await new Migration007_Refunds().ApplyAsync(db, CancellationToken.None);

        /// <summary>NOT 008: the tests that use this fixture are about 008's four columns, and 008 is applied by those tests themselves so they can watch it happen.</summary>
        /// <remarks>Почему так - `docs/decisions/cash.md`</remarks>

        await new Migration009_CashMovements().ApplyAsync(db, CancellationToken.None);
        return shiftId;
    }

    private static async Task<List<string>> ReadColumnsAsync(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}])";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
