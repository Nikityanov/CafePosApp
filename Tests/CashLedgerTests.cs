using CafePos.Core.Common;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Schema.Migrations;
using CafePos.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CafePosApp.Tests;

/// <summary>The drawer movements that never had a customer behind them: the change put in to open a shift or top it up, the cash carried out for collection, and the correcting entries that cancel either.</summary>
/// <remarks>Почему так — `docs/decisions/testing-cash.md`</remarks>

public class CashLedgerTests
{
    private const decimal LattePrice = 220m;
    private const long LatteKopecks = 22000;

    /// <summary>A café that keeps no float of its own: the owner's change lives at home.</summary>
    private const string OwnerKeepsTheFloat = "размен у владельца";

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

    private static async Task<Order> CheckoutAsync(ICheckoutService checkout, Product product, PaymentIntent payment) =>
        await checkout.CheckoutAsync(
            [new CheckoutLine(product.Id, product.Name, LattePrice, 1)],
            payment);

    private static async Task<Order> CompleteAsync(IOrderService orders, Guid orderId)
    {
        await orders.AdvanceStatusAsync(orderId);
        return await orders.AdvanceStatusAsync(orderId);
    }

    // ── Opening ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_a_shift_records_the_change_as_the_first_movement()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        var shift = await orders.OpenShiftAsync(50000);

        Assert.True(shift.IsActive);
        var movements = await host.Get<ICashLedgerService>().GetMovementsAsync(shift.Id);
        var opening = Assert.Single(movements);
        Assert.Equal(CashMovementKind.Float, opening.Kind);
        Assert.Equal(50000, opening.AmountKopecks);
        Assert.Equal(shift.Id, opening.ShiftId);
    }

    [Fact]
    public async Task A_shift_is_not_opened_twice()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await orders.OpenShiftAsync(0);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => orders.OpenShiftAsync(10000));

        // Two overlapping shifts would each have a float, an order sequence and a count, and the
        // drawer they described would be the sum of two tills — reconcilable against a figure nobody
        // could have counted.
        Assert.Contains("уже открыта", exception.Message);
    }

    /// <summary>Zero is ALLOWED, and it is not the same thing as nothing having been recorded: "I opened the till and there is none of my money in it" is a statement, and a café that runs that way has to be able to make it.</summary>

    [Fact]
    public async Task The_change_may_be_zero_but_the_movement_is_still_written()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        var shift = await orders.OpenShiftAsync(0, OwnerKeepsTheFloat);

        var opening = Assert.Single(await host.Get<ICashLedgerService>().GetMovementsAsync(shift.Id));
        Assert.Equal(0, opening.AmountKopecks);
        Assert.Equal(OwnerKeepsTheFloat, opening.Reason);
        Assert.Equal(0m, (await orders.GetShiftStatsAsync(shift.Id)).ExpectedCashNow);
    }

    [Fact]
    public async Task A_negative_change_is_refused()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        await Assert.ThrowsAsync<ValidationFailureException>(() => orders.OpenShiftAsync(-1));

        Assert.Null(await orders.GetActiveShiftAsync());
    }

    // ── The drawer figure ───────────────────────────────────────────────────────────────────────

    /// <summary>THE reason the whole feature exists. A café opens with 500 ₿ of change, takes 220 ₿ of cash sales, and finds 720 ₿ at the count. Reconciling against the payments alone would call that 500 ₿ of missing money — a shortage made entirely of the change the operator put in.</summary>

    [Fact]
    public async Task The_drawer_holds_the_change_plus_the_sales()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var shift = await orders.OpenShiftAsync(50000);
        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        var stats = await orders.GetShiftStatsAsync(shift.Id);
        Assert.Equal(500m, stats.FloatCash);
        Assert.Equal(LattePrice, stats.PaymentsCash);
        Assert.Equal(0m, stats.RefundsCash);
        Assert.Equal(0m, stats.PayoutCash);
        Assert.Equal(500m + LattePrice, stats.ExpectedCashNow);

        // And it closes as a MATCH at that count, with no reason demanded for a drawer that balanced.
        await orders.CloseShiftAsync(Money.ToKopecks(500m + LattePrice), null);
        Assert.Equal(CashDifference.Matched, (await orders.GetShiftStatsAsync(shift.Id)).Reconciliation!.Difference);
    }

    /// <summary>Cash taken out for collection lowers what the drawer is expected to hold. Were it only an audit note, the close would report a shortage of exactly the amount the operator legitimately carried away.</summary>

    [Fact]
    public async Task Cash_taken_out_for_collection_lowers_the_expectation()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var shift = await orders.OpenShiftAsync(50000);
        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        await ledger.RecordPayoutAsync(30000, "инкассация в 15:00");

        var stats = await orders.GetShiftStatsAsync(shift.Id);
        Assert.Equal(300m, stats.PayoutCash);
        Assert.Equal(500m + LattePrice - 300m, stats.ExpectedCashNow);

        await orders.CloseShiftAsync(Money.ToKopecks(500m + LattePrice - 300m), null);
        Assert.Equal(CashDifference.Matched, (await orders.GetShiftStatsAsync(shift.Id)).Reconciliation!.Difference);
    }

    /// <summary>A top-up in the middle of the shift is the same movement as the opening one.</summary>
    [Fact]
    public async Task The_change_can_be_topped_up_during_the_shift()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();

        var shift = await orders.OpenShiftAsync(0);
        await ledger.RecordFloatAsync(20000, "доложили на сдачу");

        Assert.Equal(200m, (await orders.GetShiftStatsAsync(shift.Id)).FloatCash);
        Assert.Equal(2, (await ledger.GetMovementsAsync(shift.Id)).Count);
    }

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task More_cannot_be_taken_out_than_the_drawer_holds()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();

        var shift = await orders.OpenShiftAsync(10000);

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => ledger.RecordPayoutAsync(10001, "хочу больше"));

        // Both figures, because "insufficient funds" against a drawer the operator cannot see is
        // unactionable.
        Assert.Contains("в кассе", exception.Message);
        // Nothing was written: the opening float is the only movement, so the refused collection
        // left no trace at all.
        Assert.Single(await ledger.GetMovementsAsync(shift.Id));
    }

    [Fact]
    public async Task A_collection_needs_a_reason()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await host.Get<IOrderService>().OpenShiftAsync(50000);
        var ledger = host.Get<ICashLedgerService>();

        await Assert.ThrowsAsync<ValidationFailureException>(() => ledger.RecordPayoutAsync(10000, "   "));
        await Assert.ThrowsAsync<ValidationFailureException>(() => ledger.RecordPayoutAsync(10000, null!));
    }

    [Fact]
    public async Task Taking_nothing_out_is_refused_while_putting_nothing_in_is_not()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var shift = await host.Get<IOrderService>().OpenShiftAsync(50000);
        var ledger = host.Get<ICashLedgerService>();

        // The asymmetry is the point: an opening float of 0 is a statement about the till, a
        // collection of 0 is a button pressed by accident.
        await Assert.ThrowsAsync<ValidationFailureException>(() => ledger.RecordPayoutAsync(0, "ничего"));

        await ledger.RecordFloatAsync(0);
        Assert.Equal(2, (await ledger.GetMovementsAsync(shift.Id)).Count);
    }

    [Fact]
    public async Task A_long_reason_is_refused_rather_than_truncated()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await host.Get<IOrderService>().OpenShiftAsync(500000);
        var ledger = host.Get<ICashLedgerService>();

        var exception = await Assert.ThrowsAsync<ValidationFailureException>(
            () => ledger.RecordPayoutAsync(10000, new string('я', 301)));

        // The reason is the only record of what happened. Shortening it would still look like a
        // complete answer while being a different sentence.
        Assert.Contains("обрезается не молча", exception.Message);
    }

    // ── Corrections ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wrong_collection_is_cancelled_by_a_second_row_and_the_first_survives()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var shift = await orders.OpenShiftAsync(1000000);

        await ledger.RecordPayoutAsync(300000, "инкассация");
        var wrong = (await ledger.GetMovementsAsync(shift.Id))[1];

        // 5000 entered where 50000 was meant: the money left, the number did not.
        await ledger.RecordPayoutAsync(500000, "инкассация");
        var mistaken = (await ledger.GetMovementsAsync(shift.Id)).Last();
        await ledger.ReverseMovementAsync(mistaken.Id, "описка: 5000 вместо 50000");

        var movements = await ledger.GetMovementsAsync(shift.Id);
        Assert.Equal(4, movements.Count);
        // The original row is untouched, which is the whole point of an append-only ledger.
        Assert.Equal(500000, mistaken.AmountKopecks);
        Assert.Equal("инкассация", mistaken.Reason);
        Assert.Null(mistaken.ReversesMovementId);

        var correction = movements.Last();
        Assert.Equal(mistaken.Id, correction.ReversesMovementId);
        // The opposite KIND, because what physically happened is that money went back IN. Recording
        // it as a reversed payout would say the opposite about direction while netting correctly.
        Assert.Equal(CashMovementKind.Float, correction.Kind);
        Assert.Equal(500000, correction.AmountKopecks);

        // 10000 (float) − 3000 (the real collection) = 7000: the drawer is exactly what it was before
        // the mistaken collection, and both wrong rows are still on record.
        Assert.Equal(7000m, (await orders.GetShiftStatsAsync(shift.Id)).ExpectedCashNow);
        Assert.Equal(15000m, (await orders.GetShiftStatsAsync(shift.Id)).FloatCash);
        Assert.Equal(8000m, (await orders.GetShiftStatsAsync(shift.Id)).PayoutCash);
    }

    [Fact]
    public async Task A_movement_can_be_cancelled_only_once()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var shift = await orders.OpenShiftAsync(500000);

        await ledger.RecordPayoutAsync(100000, "инкассация");
        var movement = (await ledger.GetMovementsAsync(shift.Id)).Last();
        await ledger.ReverseMovementAsync(movement.Id, "первая отмена");

        // Without this, two rows could each claim to undo the same movement and the drawer would be
        // credited twice for one mistake.
        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => ledger.ReverseMovementAsync(movement.Id, "вторая отмена"));
        Assert.Contains("уже отменено", exception.Message);

        // And a correction is itself not correctable.
        var correction = (await ledger.GetMovementsAsync(shift.Id)).Last();
        await Assert.ThrowsAsync<ConflictException>(
            () => ledger.ReverseMovementAsync(correction.Id, "отменить отмену"));

        // The collection and its cancellation cancel each other out, so the drawer is back to the float
        // alone — with the correction counted as money that came back IN.
        Assert.Equal(5000m, (await orders.GetShiftStatsAsync(shift.Id)).ExpectedCashNow);
        Assert.Equal(1000m, (await orders.GetShiftStatsAsync(shift.Id)).PayoutCash);
    }

    // ── The closed shift ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_closed_shift_takes_no_further_movements()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var shift = await orders.OpenShiftAsync(500000);
        await orders.CloseShiftAsync(500000, null);

        // These four columns on the shift are write-once, and a movement is the same kind of
        // statement about the same drawer: accepting one after the count would let the drawer the
        // count was taken against change afterwards, with nothing recording that it did.
        var collection = await Assert.ThrowsAsync<ConflictException>(
            () => ledger.RecordPayoutAsync(10000, "инкассация после закрытия"));
        Assert.Contains("Смена не открыта", collection.Message);

        await Assert.ThrowsAsync<ConflictException>(() => ledger.RecordFloatAsync(10000));
        await Assert.ThrowsAsync<ConflictException>(() => ledger.RecordPayoutAsync(10000, "опять"));

        var any = await ledger.GetMovementsAsync(shift.Id);
        await Assert.ThrowsAsync<ConflictException>(() => ledger.ReverseMovementAsync(any.Last().Id, "опять"));
    }

    [Fact]
    public async Task A_correction_cannot_reach_into_another_shift()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var first = await orders.OpenShiftAsync(500000);
        var movement = (await ledger.GetMovementsAsync(first.Id)).Single();
        await orders.CloseShiftAsync(500000, null);

        await orders.OpenShiftAsync(0);

        // The mistake belongs to the drawer it was made in. Cancelling it into the next shift would
        // split one mistake across two reports and leave the first one wrong.
        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => ledger.ReverseMovementAsync(movement.Id, "из прошлой смены"));
        Assert.Contains("другой смене", exception.Message);
    }

    // ── Carrying over ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_next_shift_is_pre_filled_with_what_the_last_count_found()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        Assert.Null(await orders.GetLastCountedCashKopecksAsync());

        // Counted LESS than expected on purpose: what carries over is what was physically there,
        // not what the ledger wished were there.
        await orders.OpenShiftAsync(500000);
        await orders.CloseShiftAsync(420000, "часть размена забрали");

        Assert.Equal(420000, await orders.GetLastCountedCashKopecksAsync());
    }

    [Fact]
    public async Task A_shift_closed_without_a_count_offers_nothing_to_carry_over()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();

        // A hand-edited or imported row: nothing was counted, so there is no figure to suggest and
        // the operator types it.
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Shifts.Add(new Shift
            {
                Id = Guid.NewGuid(),
                StartTime = DateTimeOffset.UtcNow.AddDays(-1),
                EndTime = DateTimeOffset.UtcNow,
                IsActive = false,
                NextOrderNumber = 1
            });
            await db.SaveChangesAsync();
        }

        Assert.Null(await orders.GetLastCountedCashKopecksAsync());
    }

    [Fact]
    public async Task An_archive_export_works_with_no_shift_open()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        await orders.OpenShiftAsync(500000);
        await orders.CloseShiftAsync(500000, null);

        // A terminal between shifts used to be impossible, and the export used to create a shift to
        // have something to write about — which would have produced a report of an empty shift on the
        // one day somebody is most likely to take an archive.
        Assert.Null(await orders.GetActiveShiftAsync());

        var path = await host.Get<IBackupService>().ExportArchiveAsync();
        Assert.True(File.Exists(path));
    }

    // ── The negative balance ───────────────────────────────────────────────────────────────────

    /// <summary>The one case the code cannot prevent, decided rather than discovered: a refund taken AFTER a collection leaves money the drawer no longer holds. Every step is legitimate — the refund rules are the carefully tested part of this app, and physically the money comes from a reserve outside the drawer — so the balance goes negative and is shown rather than blocked.</summary>

    [Fact]
    public async Task A_refund_after_a_collection_may_leave_the_drawer_negative_and_is_reported()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var orders = host.Get<IOrderService>();
        var ledger = host.Get<ICashLedgerService>();
        var product = await SeedLatteAsync(host.Get<ICatalogService>());

        var shift = await orders.OpenShiftAsync(0);
        var order = await CheckoutAsync(host.Get<ICheckoutService>(), product, new PaymentIntent(LattePrice, PaymentMethod.Cash));
        await CompleteAsync(orders, order.Id);

        // The whole drawer, out of the door. Allowed: it was in the drawer a moment ago.
        await ledger.RecordPayoutAsync(LatteKopecks, "инкассация");
        Assert.Equal(0m, (await orders.GetShiftStatsAsync(shift.Id)).ExpectedCashNow);

        await orders.RefundAsync(order.Id, LattePrice, "возврат");

        var stats = await orders.GetShiftStatsAsync(shift.Id);
        Assert.Equal(-LattePrice, stats.ExpectedCashNow);

        // A count cannot be negative, so such a shift closes as an overage with a reason demanded —
        // which is the right thing to make a human look at.
        var exception = await Assert.ThrowsAsync<ValidationFailureException>(
            () => orders.CloseShiftAsync(0, null));
        Assert.Contains("сходится с учётными данными", exception.Message);
    }

    // ── The schema ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The upgrade path for version 9. The three columns that would carry a history cannot be reconstructed for a shift that has already closed, so the table stays EMPTY: an empty table says "this terminal did not record drawer movements before version 9", which is true, whereas a backfilled figure would be money nobody ever counted sitting in the one table an auditor reads expecting exactly that.</summary>

    [Fact]
    public async Task Migration_009_creates_the_table_and_leaves_history_empty()
    {
        using var host = TestHost.Create();
        var factory = host.Get<IDbContextFactory<AppDbContext>>();
        Guid shiftId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            await SqliteSchemaHelper.ExecuteAsync(db, SchemaMigrationTests.LegacyV1Schema, CancellationToken.None);
            foreach (var migration in SchemaMigrator.AllMigrations.Where(migration => migration.Version <= 8))
                await migration.ApplyAsync(db, CancellationToken.None);

            shiftId = Guid.NewGuid();
            await SqliteSchemaHelper.ExecuteAsync(db,
                $"INSERT INTO [Shifts] ([Id], [StartTime], [IsActive], [NextOrderNumber]) " +
                $"VALUES ('{shiftId.ToString("D").ToUpperInvariant()}', '2026-09-20 08:00:00.0000000+00:00', 1, 1)",
                CancellationToken.None);

            await new Migration009_CashMovements().ApplyAsync(db, CancellationToken.None);
        }

        await using (var db = await factory.CreateDbContextAsync())
        {
            // Idempotent: a re-run after a partial failure must not fail on the second CREATE.
            await new Migration009_CashMovements().ApplyAsync(db, CancellationToken.None);

            Assert.Equal(0, await db.CashMovements.CountAsync());
            var shift = await db.Shifts.AsNoTracking().SingleAsync(row => row.Id == shiftId);
            Assert.Null(shift.CountedCashKopecks);
        }

        // And the domain reads the untouched shift as a drawer of zero rather than failing on the
        // table it has just gained.
        var stats = await host.Get<IOrderService>().GetShiftStatsAsync(shiftId);
        Assert.Equal(0m, stats.FloatCash);
        Assert.Equal(0m, stats.ExpectedCashNow);
    }
}
