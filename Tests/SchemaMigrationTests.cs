using CafePos.Core.Data;
using CafePos.Core.Schema;
using CafePos.Core.Schema.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CafePosApp.Tests;

public class SchemaMigrationTests
{
    // Simulate the very first release: money as TEXT, local timestamps, no audit tables.
    internal const string LegacyV1Schema = """
        CREATE TABLE [Products] ([Id] TEXT NOT NULL PRIMARY KEY, [Name] TEXT NOT NULL, [Price] TEXT NOT NULL);
        CREATE TABLE [ModifierGroups] ([Id] TEXT NOT NULL PRIMARY KEY, [Name] TEXT NOT NULL);
        CREATE TABLE [ModifierOptions] ([Id] TEXT NOT NULL PRIMARY KEY, [Name] TEXT NOT NULL, [ModifierGroupId] TEXT NOT NULL);
        CREATE TABLE [Shifts] ([Id] TEXT NOT NULL PRIMARY KEY, [StartTime] TEXT NOT NULL, [EndTime] TEXT NULL, [IsActive] INTEGER NOT NULL);
        CREATE TABLE [Orders] ([Id] TEXT NOT NULL PRIMARY KEY, [CreatedAt] TEXT NOT NULL, [TotalPrice] TEXT NOT NULL,
            [Status] INTEGER NOT NULL DEFAULT 0, [ShiftId] TEXT NULL, [OrderNumber] INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE [OrderItems] ([Id] TEXT NOT NULL PRIMARY KEY, [OrderId] TEXT NOT NULL, [ProductId] TEXT NOT NULL,
            [ProductName] TEXT NOT NULL, [Price] TEXT NOT NULL, [Quantity] INTEGER NOT NULL, [SelectedModifierName] TEXT NULL);
        """;

    [Fact]
    public async Task Fresh_database_gets_the_latest_schema()
    {
        using var host = TestHost.Create();
        var migrator = host.Get<SchemaMigrator>();

        var result = await migrator.MigrateAsync();

        Assert.True(result.FreshDatabase);
        Assert.Equal(migrator.LatestVersion, result.FinalVersion);
        // Pinned on purpose: when the next migration lands this line is the reminder that the
        // expectations above it are no longer enough. 13 = Orders.SeenAt.
        Assert.Equal(13, result.FinalVersion);
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        using var host = TestHost.Create();
        var migrator = host.Get<SchemaMigrator>();

        await migrator.MigrateAsync();
        var second = await migrator.MigrateAsync();

        Assert.False(second.FreshDatabase);
        Assert.Empty(second.AppliedMigrations);
        Assert.Equal(migrator.LatestVersion, second.FinalVersion);
    }

    [Fact]
    public async Task Legacy_v1_database_is_upgraded_to_the_latest_version()
    {
        using var host = TestHost.Create();
        var factory = host.Get<IDbContextFactory<AppDbContext>>();

        // Then run the baseline (v1) on top, exactly like the real upgrade path does.
        await using (var db = await factory.CreateDbContextAsync())
        {
            await SqliteSchemaHelper.ExecuteAsync(db, LegacyV1Schema, CancellationToken.None);

            var baseline = new Migration001_Baseline();
            await baseline.ApplyAsync(db, CancellationToken.None);
            await SqliteSchemaHelper.ExecuteAsync(db,
                "CREATE TABLE IF NOT EXISTS [SchemaVersions] ([Version] INTEGER NOT NULL CONSTRAINT [PK_SchemaVersions] PRIMARY KEY, [Name] TEXT NOT NULL, [AppliedAt] TEXT NOT NULL)",
                CancellationToken.None);
            await SqliteSchemaHelper.ExecuteAsync(db,
                "INSERT INTO [SchemaVersions] ([Version], [Name], [AppliedAt]) VALUES (1, 'Baseline', '2025-01-01T00:00:00+00:00')",
                CancellationToken.None);

            // Legacy data: TEXT money and a local timestamp without an offset.
            await SqliteSchemaHelper.ExecuteAsync(db, """
                INSERT INTO [Products] ([Id], [Name], [Price]) VALUES ('p1', 'Латте', '250.5');
                INSERT INTO [Shifts] ([Id], [StartTime], [IsActive]) VALUES ('s1', '2026-09-20 08:00:00', 1);
                INSERT INTO [Orders] ([Id], [CreatedAt], [TotalPrice], [Status], [ShiftId], [OrderNumber])
                    VALUES ('o1', '2026-09-20 09:15:00', '501', 3, 's1', 1);
                INSERT INTO [OrderItems] ([Id], [OrderId], [ProductId], [ProductName], [Price], [Quantity])
                    VALUES ('i1', 'o1', 'p1', 'Латте', '250.5', 2);
                """, CancellationToken.None);
        }

        var migrator = host.Get<SchemaMigrator>();
        var result = await migrator.MigrateAsync();

        Assert.False(result.FreshDatabase);
        Assert.Equal(migrator.LatestVersion, result.FinalVersion);
        Assert.Equal(12, result.AppliedMigrations.Count); // 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13

        // Money became integer kopecks; totals were recalculated from the order items.
        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        Assert.Equal(25050, await ScalarLongAsync(connection, "SELECT [PriceKopecks] FROM [Products] WHERE [Id] = 'p1'"));
        Assert.Equal(50100, await ScalarLongAsync(connection, "SELECT [TotalKopecks] FROM [Orders] WHERE [Id] = 'o1'"));
        // Timestamps carry an explicit UTC offset now.
        var createdAt = await ScalarTextAsync(connection, "SELECT [CreatedAt] FROM [Orders] WHERE [Id] = 'o1'");
        Assert.EndsWith("+00:00", createdAt);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = new SqliteCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = new SqliteCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }

    [Fact]
    public async Task Migrated_database_has_the_money_and_audit_columns()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();

        Assert.Contains("PriceKopecks", await GetColumnsAsync(connection, "Products"));
        Assert.Contains("TotalKopecks", await GetColumnsAsync(connection, "Orders"));
        Assert.Contains("OrderNumber", await GetColumnsAsync(connection, "Orders"));
        Assert.Contains("IsDeleted", await GetColumnsAsync(connection, "Products"));
        Assert.Contains("NextOrderNumber", await GetColumnsAsync(connection, "Shifts"));
        Assert.Contains("PaidKopecks", await GetColumnsAsync(connection, "Orders"));
        Assert.Contains("AmountKopecks", await GetColumnsAsync(connection, "OrderPayments"));
        Assert.Contains("IsRefund", await GetColumnsAsync(connection, "OrderPayments"));
        Assert.Contains("Note", await GetColumnsAsync(connection, "OrderPayments"));
        Assert.Contains("CountedCashKopecks", await GetColumnsAsync(connection, "Shifts"));
        Assert.Contains("ExpectedCashKopecks", await GetColumnsAsync(connection, "Shifts"));
        Assert.Contains("ReconciledAt", await GetColumnsAsync(connection, "Shifts"));
        Assert.Contains("CashDiscrepancyReason", await GetColumnsAsync(connection, "Shifts"));
        Assert.Contains("PriceKopecks", await GetColumnsAsync(connection, "Combos"));
    }

    /// <summary>
    /// The backfill of <c>Combos.PriceKopecks</c> is the honest one and it is worth a test of its own:
    /// the sum of the slots, because that is what those bundles were in fact charging. A database that
    /// predates version 12 is built here by running the migrations up to 11 by hand, a bundle with
    /// known slots is inserted while the column does not yet exist, and only then is version 12 applied.
    /// Anything else — 0, a round number, the dish price of the first slot — would be a number nobody
    /// charged, and the whole point of the test is that the upgrade changes no price.
    /// </summary>
    [Fact]
    public async Task An_existing_bundle_is_backfilled_with_the_price_it_was_actually_charging()
    {
        using var host = TestHost.Create();
        var factory = host.Get<IDbContextFactory<AppDbContext>>();

        var espresso = "33333333-3333-3333-3333-333333333333";
        var croissant = "44444444-4444-4444-4444-444444444444";
        var syrup = "55555555-5555-5555-5555-555555555555";
        var combo = "66666666-6666-6666-6666-666666666666";

        await using (var db = await factory.CreateDbContextAsync())
        {
            await SqliteSchemaHelper.ExecuteAsync(db, LegacyV1Schema, CancellationToken.None);

            // The real migrations, up to 11 — the terminal this test is about has been in service long
            // enough to have bundles with slots, and a hand-written Combos table would test nothing.
            foreach (var migration in SchemaMigrator.AllMigrations.Where(migration => migration.Version <= 11))
                await migration.ApplyAsync(db, CancellationToken.None);

            await SqliteSchemaHelper.ExecuteAsync(db,
                "CREATE TABLE IF NOT EXISTS [SchemaVersions] ([Version] INTEGER NOT NULL CONSTRAINT [PK_SchemaVersions] PRIMARY KEY, [Name] TEXT NOT NULL, [AppliedAt] TEXT NOT NULL)",
                CancellationToken.None);
            for (var version = 1; version <= 11; version++)
                await SqliteSchemaHelper.ExecuteAsync(db,
                    $"INSERT INTO [SchemaVersions] ([Version], [Name], [AppliedAt]) VALUES ({version}, 'seed', '2025-01-01T00:00:00+00:00')",
                    CancellationToken.None);

            // Three slots covering all three price states: a dish price (NULL slot price), a free slot
            // (0) and a doubled slot (QuantityPerUnit = 2). 120 + 0 + 2 × 30 = 180 ₽ — the sum, and
            // therefore the price this bundle has in fact been charging all along.
            await SqliteSchemaHelper.ExecuteAsync(db, $"""
                INSERT INTO [Products] ([Id], [Name], [PriceKopecks]) VALUES
                    ('{espresso}', 'Эспрессо', 12000),
                    ('{croissant}', 'Круассан', 18000),
                    ('{syrup}', 'Сироп', 3000);
                INSERT INTO [Combos] ([Id], [Name], [IsDeleted], [SortOrder]) VALUES ('{combo}', 'Набор', 0, 0);
                INSERT INTO [ComboComponents] ([Id], [ComboId], [ProductId], [QuantityPerUnit], [ComponentPriceKopecks], [SubstituteProductId]) VALUES
                    ('77777777-7777-7777-7777-777777777777', '{combo}', '{espresso}', 1, NULL, NULL),
                    ('88888888-8888-8888-8888-888888888888', '{combo}', '{croissant}', 1, 0, NULL),
                    ('99999999-9999-9999-9999-999999999999', '{combo}', '{syrup}', 2, NULL, NULL);
                """, CancellationToken.None);
        }

        var migrator = host.Get<SchemaMigrator>();
        var result = await migrator.MigrateAsync();

        // Ровно ДВЕ миграции доехали — 012 и та, что добавлена после неё, — значит тест действительно
        // проверяет 012, а не «схема целиком». Список назван явно, а не по числу, иначе следующая
        // миграция сделала бы тест зелёным на пустой базе.
        Assert.Equal(
            [new Migration012_ComboOwnPrice().Name, new Migration013_OrderSeenAt().Name],
            result.AppliedMigrations);
        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        Assert.Equal(18000, await ScalarLongAsync(connection, $"SELECT [PriceKopecks] FROM [Combos] WHERE [Id] = '{combo}'"));
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}])";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>
    /// Fresh and upgraded installs are provisioned from two unrelated sources: a fresh database
    /// takes its DDL from EnsureCreated (generated out of the EF model), an already provisioned
    /// one from the hand-written SQL in Core/Schema/Migrations. Nothing else keeps them in step,
    /// so a property added to an entity without a migration ships a working fresh install and
    /// breaks every device that was set up before it — a POS with no cloud backup to restore from.
    /// The database here is built by the migrations alone, and it has to match the EF model
    /// column for column: no mapped column may be missing, and no unmapped column may linger.
    /// </summary>
    [Fact]
    public async Task Ef_model_and_migrations_agree_on_every_column()
    {
        using var host = TestHost.Create();
        var factory = host.Get<IDbContextFactory<AppDbContext>>();

        // Ставим базу так, как её создавала самая первая версия приложения: только на этом пути
        // DDL берётся из рукописных миграций, а не генерируется EnsureCreated-ом из модели.
        await using (var db = await factory.CreateDbContextAsync())
            await SqliteSchemaHelper.ExecuteAsync(db, LegacyV1Schema, CancellationToken.None);

        var migrator = host.Get<SchemaMigrator>();
        var result = await migrator.MigrateAsync();

        // На пустой базе мигратор вызвал бы EnsureCreated, и обе стороны сравнения гарантированно
        // совпали бы — тест ничего бы не проверял. Такая база обязана идти по ветке апгрейда.
        Assert.False(result.FreshDatabase);
        Assert.Equal(migrator.LatestVersion, result.FinalVersion);

        int entityTypeCount;
        Dictionary<string, HashSet<string>> modelColumns;
        await using (var db = await factory.CreateDbContextAsync())
        {
            // GetEntityTypes() отдаёт IEnumerable, а не материализованную коллекцию: .Count здесь
            // — метод-группа Enumerable, а не свойство. Материализуем один раз, чтобы и счётчик,
            // и разбор колонок смотрели на один и тот же список типов сущностей.
            var entityTypes = db.Model.GetEntityTypes().ToArray();
            entityTypeCount = entityTypes.Length;
            modelColumns = GetModelColumns(entityTypes);
        }

        await using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();

        var databaseTables = await GetAppTablesAsync(connection);
        var comparedColumns = 0;

        foreach (var table in modelColumns.Keys.Union(databaseTables, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            Assert.True(databaseTables.Contains(table),
                $"Таблица [{table}] отображена в EF-модели, но ни одна миграция её не создаёт.");

            var actual = await GetColumnsAsync(connection, table);
            var expected = modelColumns.GetValueOrDefault(table) ?? new HashSet<string>(StringComparer.Ordinal);
            comparedColumns += actual.Count;

            // Модель -> база: колонка сопоставлена в EF, но миграция её не создала.
            // Ровно этот случай даёт «no such column: X» на уже provisioned терминале.
            var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0,
                $"[{table}]: миграции не создали колонки, которые сопоставлены в EF-модели: {string.Join(", ", missing)}");

            // База -> модель: колонка, которую никто не читает и не пишет. Ловит и CreateTable,
            // который создал лишнее, и забытый хвост от удалённого свойства.
            var extra = actual.Except(expected, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.True(extra.Length == 0,
                $"[{table}]: в базе есть колонки, которых нет в EF-модели: {string.Join(", ", extra)}");
        }

        // Страховка от вырожденного прогона: разбор модели или PRAGMA не должны молча вернуть
        // пустоту и превратить все проверки выше в безусловно проходящие.
        Assert.Equal(entityTypeCount, modelColumns.Count);
        Assert.True(comparedColumns > 60, $"Сравнено всего {comparedColumns} колонок — тест выродился.");
    }

    /// <summary>
    /// Table and column names taken straight from the EF relational metadata, so [Table]/[Column]
    /// attributes, the ToTable()/HasColumnName() calls in OnModelCreating and the naming conventions
    /// are all applied by EF itself. Shadow properties count as columns: EF does create a column
    /// for a foreign key that has no matching CLR field.
    /// </summary>
    private static Dictionary<string, HashSet<string>> GetModelColumns(IEnumerable<IEntityType> entityTypes)
    {
        var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entityType in entityTypes)
        {
            if (entityType.GetTableName() is not { } table) continue; // не хранится в собственной таблице
            if (!columns.TryGetValue(table, out var tableColumns))
                columns[table] = tableColumns = new HashSet<string>(StringComparer.Ordinal);

            // GetColumnName() принимает StoreObjectIdentifier по ссылке (in), а Create() отдаёт
            // nullable — этот nullable ушёл бы дальше и сломал бы вызов. Собираем идентификатор
            // напрямую из уже проверенного имени таблицы: StoreObjectIdentifier.Table() — это ровно
            // то, чем Create() конструирует идентификатор таблицы, только без обёртки. Отдельного
            // отката на GetColumnBaseName() не нужно — для валидного store object возвращает то же.
            var storeObject = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName(storeObject) is { } column)
                    tableColumns.Add(column);
            }
        }

        return columns;
    }

    /// <summary>Tables the app owns. sqlite_* bookkeeping and the EF migrations history are not ours.</summary>
    private static async Task<HashSet<string>> GetAppTablesAsync(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }
}
