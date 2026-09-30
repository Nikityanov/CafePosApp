namespace CafePos.Core.Data;

/// <summary>Where the local database and its backups live (provided by the platform project).</summary>
public sealed class DatabaseOptions
{
    public required string DatabasePath { get; init; }
    public required string BackupDirectory { get; init; }

    /// <summary>How many automatic backups are kept before the oldest ones are deleted.</summary>
    public int BackupRetention { get; init; } = 10;

    /// <summary>Automatic backup is taken at most once per this interval.</summary>
    public TimeSpan AutomaticBackupInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Seed a small demo menu the first time the app runs on an empty database.</summary>
    public bool SeedDemoData { get; init; } = true;

    public string ConnectionString => $"Data Source={DatabasePath}";
}
