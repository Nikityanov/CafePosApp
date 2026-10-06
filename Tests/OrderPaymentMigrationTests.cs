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

/// <summary>Migrations 006 and 007: the backfill of PaidKopecks, the synthetic payment row beside it, and the refund columns.</summary>
/// <remarks>
/// Split out of a single 1 319-line file that held this, the ledger, refunds, cancellation
/// stock, shift closing and two schema migrations in one class.
/// </remarks>
public class OrderPaymentMigrationTests
{

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
            // Versions 7 to 11 as well: the point of reading the result back through EF is to prove
            // the upgraded database is usable by the current model, and a v6-only schema is not what
            // any device actually runs.
            await BringSchemaToCurrentAsync(db, afterVersion: 6);
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
        {
            await new Migration006_OrderPayments().ApplyAsync(db, CancellationToken.None);
            await BringSchemaToCurrentAsync(db, afterVersion: 6);
        }

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
            await BringSchemaToCurrentAsync(db, afterVersion: 6);
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

        // A refund row written after the upgrade lands in a database that already has the rows AND is
        // readable by the current model, which is the only shape any device runs.
        await using (var db = await factory.CreateDbContextAsync())
            await BringSchemaToCurrentAsync(db, afterVersion: 7);

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
    private  static async Task BuildV5DatabaseAsync(TestHost.Host host, (Guid Id, long TotalKopecks, string Status)[] orders)
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
    /// Applies every migration newer than <paramref name="afterVersion"/> to a database a test has
    /// stopped part-way through, so that EF can read it afterwards.
    /// <para>
    /// WHY THIS IS HERE AND NOT <c>SchemaMigrator.MigrateAsync</c>: these tests each apply one
    /// migration to a database that holds real rows, on purpose, to see what that one statement does.
    /// Reading the result back through EF is the point — it proves the upgraded database is usable by
    /// the CURRENT model — and the current model selects every column, so a database left at version 6
    /// or 7 no longer has them all ("no such column: o.CustomerPhone"). Applying the remaining
    /// migrations is what a real device does anyway: no terminal ever stops halfway, so the state these
    /// tests were reading was a state nothing runs. The alternative, reading the columns back with raw
    /// SQL, would quietly stop testing that the model and the schema still fit together.
    /// </para>
    /// <para>
    /// Applied in version order and never through SchemaVersions, so the versions this test controls
    /// stay the ones it decided on.
    /// </para>
    /// </summary>
    private  static async Task BringSchemaToCurrentAsync(AppDbContext db, int afterVersion)
    {
        foreach (var migration in SchemaMigrator.AllMigrations
                     .Where(migration => migration.Version > afterVersion)
                     .OrderBy(migration => migration.Version))
        {
            await migration.ApplyAsync(db, CancellationToken.None);
        }
    }
}
