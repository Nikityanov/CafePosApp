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
/// block, the cancel/refund guard and the migration that backfills the column and the ledger of
/// every order that existed before the feature.
/// </summary>
public class OrderPaymentTests
{
    private const decimal LattePrice = 220m;
    private const long LatteKopecks = 22000;

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

    private static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, PaymentIntent? payment = null) =>
        payment is null
            ? await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, 1)])
            : await checkout.CheckoutAsync([new CheckoutLine(product.Id, product.Name, LattePrice, 1)], payment);

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
    /// The invariant every screen and every report leans on, checked on all four write paths at
    /// once (checkout with payment, deferred checkout, partial payments, over-tendered cash):
    /// the scalar equals the sum of the ledger rows, and it never exceeds the order total.
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

        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var expected = new Dictionary<Guid, long>
        {
            [paid.Id] = LatteKopecks,
            [deferred.Id] = 0,
            [partial.Id] = 5000,
            [overpaid.Id] = LatteKopecks,
            [cancelled.Id] = 0
        };

        foreach (var order in await db.Orders.ToListAsync())
        {
            var ledger = await db.OrderPayments.Where(payment => payment.OrderId == order.Id).SumAsync(payment => payment.AmountKopecks);
            Assert.Equal(expected[order.Id], order.PaidKopecks);
            Assert.Equal(order.PaidKopecks, ledger);
            Assert.True(order.PaidKopecks <= order.TotalKopecks, $"Order {order.OrderNumber} recorded more than it is worth.");
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

    [Fact]
    public async Task Cancelling_a_paid_order_is_refused_so_the_sale_cannot_vanish()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));

        await Assert.ThrowsAsync<ConflictException>(() => orders.CancelOrderAsync(order.Id));

        var reloaded = await orders.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.InProgress, reloaded!.Status);
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
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);

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
}
