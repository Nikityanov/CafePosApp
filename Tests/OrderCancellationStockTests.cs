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

/// <summary>What cancelling an order does to the shelf: the write-off undone, the journal inverted, the ingredient protected.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class OrderCancellationStockTests
{

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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
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
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var catalog = host.Get<ICatalogService>();
        var checkout = host.Get<ICheckoutService>();
        var (product, milk) = await SeedLatteWithMilkAsync(catalog);

        await CheckoutAsync(checkout, product);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => catalog.DeleteIngredientAsync(milk.Id));

        Assert.Contains("Молоко", exception.Message);
        Assert.NotNull(await catalog.GetIngredientAsync(milk.Id));
    }
}
