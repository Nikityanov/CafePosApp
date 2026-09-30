namespace CafePos.Core.Services;

/// <summary>A local backup file of the SQLite database.</summary>
public sealed record BackupInfo(string FilePath, string FileName, DateTimeOffset CreatedAt, long SizeBytes)
{
    public string SizeText => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024.0:F0} КБ"
        : $"{SizeBytes / (1024.0 * 1024):F1} МБ";
}

/// <summary>Result of checking a backup file before restoring it.</summary>
public sealed record BackupValidation(bool IsValid, int SchemaVersion, string Message);

public interface IBackupService
{
    Task<BackupInfo> CreateBackupAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>Creates a backup when the last one is older than the configured interval.</summary>
    Task<BackupInfo?> EnsureAutomaticBackupAsync(CancellationToken cancellationToken = default);

    Task<List<BackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks that a file is a usable CafePOS database.</summary>
    Task<BackupValidation> ValidateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Replaces the current database with the backup. The app must be restarted afterwards.</summary>
    Task RestoreAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Builds a ZIP with the database copy, products, sales and the shift report.</summary>
    Task<string> ExportArchiveAsync(CancellationToken cancellationToken = default);
}
