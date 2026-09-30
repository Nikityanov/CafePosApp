using System.IO.Compression;
using System.Text;
using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

public sealed class BackupService(
    IDbContextFactory<AppDbContext> factory,
    DatabaseOptions options,
    SchemaMigrator migrator,
    ICatalogService catalog,
    IReportExportService reports,
    IOrderService orders,
    TimeProvider timeProvider,
    ILogger<BackupService> logger) : IBackupService
{
    private const string BackupFilePrefix = "cafe-pos-";
    private const string BackupFileExtension = ".db3";

    public async Task<BackupInfo> CreateBackupAsync(string reason, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.BackupDirectory);
        var now = timeProvider.GetUtcNow();
        var fileName = $"{BackupFilePrefix}{now.ToLocalTime():yyyyMMdd-HHmmss}{BackupFileExtension}";
        var filePath = Path.Combine(options.BackupDirectory, fileName);
        if (File.Exists(filePath)) File.Delete(filePath);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // VACUUM INTO produces a consistent snapshot while the app keeps running.
        await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{EscapeLiteral(filePath)}'", cancellationToken);

        var info = new BackupInfo(filePath, fileName, now, new FileInfo(filePath).Length);
        logger.LogInformation("Backup created ({Reason}): {FileName} ({Size})", reason, fileName, info.SizeText);
        Rotate();
        return info;
    }

    public async Task<BackupInfo?> EnsureAutomaticBackupAsync(CancellationToken cancellationToken = default)
    {
        var backups = await GetBackupsAsync(cancellationToken);
        var newest = backups.FirstOrDefault();
        if (newest is not null && timeProvider.GetUtcNow() - newest.CreatedAt < options.AutomaticBackupInterval) return null;
        return await CreateBackupAsync("Автоматически", cancellationToken);
    }

    public Task<List<BackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(options.BackupDirectory)) return Task.FromResult(new List<BackupInfo>());

        var backups = Directory.EnumerateFiles(options.BackupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
            .Select(path => new FileInfo(path))
            .Select(file => new BackupInfo(file.FullName, file.Name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), file.Length))
            .OrderByDescending(info => info.CreatedAt)
            .ToList();

        return Task.FromResult(backups);
    }

    public async Task<BackupValidation> ValidateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new BackupValidation(false, 0, "файл не найден");

        try
        {
            await using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA integrity_check", cancellationToken).ConfigureAwait(false));
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return new BackupValidation(false, 0, $"проверка целостности не пройдена ({integrity})");

            var tables = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Products','Orders','Shifts')", cancellationToken).ConfigureAwait(false));
            if (tables < 3) return new BackupValidation(false, 0, "это не база CafePOS");

            var hasVersions = Convert.ToInt64(await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaVersions'", cancellationToken).ConfigureAwait(false));
            var version = hasVersions == 0
                ? 0
                : Convert.ToInt32(await ScalarAsync(connection, "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersions", cancellationToken).ConfigureAwait(false));

            if (version > migrator.LatestVersion)
                return new BackupValidation(false, version, $"копия создана более новой версией приложения (схема v{version})");

            return new BackupValidation(true, version, "копия пригодна к восстановлению");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Backup validation failed for {File}", filePath);
            return new BackupValidation(false, 0, exception.Message);
        }
    }

    public async Task RestoreAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(filePath, cancellationToken);
        if (!validation.IsValid)
            throw new ValidationFailureException($"Резервную копию нельзя восстановить: {validation.Message}.");

        try
        {
            var safety = await CreateBackupAsync("Перед восстановлением", cancellationToken);
            logger.LogInformation("Safety copy before restore: {FileName}", safety.FileName);
        }
        catch (Exception exception)
        {
            // A corrupted database cannot be vacuumed, and that must not block the restore.
            logger.LogWarning(exception, "Could not create a safety copy before restoring");
        }

        File.Copy(filePath, options.DatabasePath, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = options.DatabasePath + suffix;
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        logger.LogWarning("Database restored from {File}", filePath);
    }

    public async Task<string> ExportArchiveAsync(CancellationToken cancellationToken = default)
    {
        var backup = await CreateBackupAsync("Экспорт данных", cancellationToken);
        var productsCsv = await catalog.ExportProductsCsvAsync(cancellationToken);
        var salesCsv = await reports.ExportSalesCsvAsync(cancellationToken);
        var shift = await orders.GetOrCreateActiveShiftAsync(cancellationToken);
        var shiftCsv = await reports.ExportShiftReportCsvAsync(shift.Id, cancellationToken);

        var exportDirectory = Path.Combine(options.BackupDirectory, "exports");
        Directory.CreateDirectory(exportDirectory);
        var archivePath = Path.Combine(exportDirectory,
            $"cafe-pos-export-{timeProvider.GetUtcNow().ToLocalTime():yyyyMMdd-HHmmss}.zip");

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(backup.FilePath, Path.GetFileName(backup.FilePath), CompressionLevel.Optimal);
        WriteTextEntry(archive, "products.csv", productsCsv);
        WriteTextEntry(archive, "sales.csv", salesCsv);
        WriteTextEntry(archive, "shift-report.csv", shiftCsv);
        WriteTextEntry(archive, "readme.txt",
            $"""
            Экспорт данных CafePOS
            Дата: {timeProvider.GetUtcNow().ToLocalTime():dd.MM.yyyy HH:mm}
            Файлы:
              * {Path.GetFileName(backup.FilePath)} — полная копия базы данных (восстанавливается в Настройках)
              * products.csv — каталог товаров
              * sales.csv — журнал продаж
              * shift-report.csv — отчёт текущей смены
            """);

        logger.LogInformation("Data exported to {File}", archivePath);
        return archivePath;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(content);
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''");

    /// <summary>Keeps only the newest <see cref="DatabaseOptions.BackupRetention"/> backups.</summary>
    private void Rotate()
    {
        var retention = Math.Max(1, options.BackupRetention);
        var stale = Directory.EnumerateFiles(options.BackupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(retention)
            .ToList();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
                logger.LogInformation("Old backup removed: {FileName}", file.Name);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete old backup {FileName}", file.Name);
            }
        }
    }
}

