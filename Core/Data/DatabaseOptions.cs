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

    /// <summary>
    /// Minutes an order with no requested time is promised to take — what the menu shows as "about
    /// 10 minutes" and what the queue sorts against. Assigned once at startup to
    /// <see cref="Models.Order.LeadTimeMinutes"/>.
    /// <para>
    /// It lives here and not in a table of settings because the app has no settings store, and a new
    /// table is not the price of one number. It is read at startup and never from the database, so an
    /// order can never have its promise recomputed after the fact.
    /// </para>
    /// </summary>
    public int LeadTimeMinutes { get; init; } = Models.Order.DefaultLeadTimeMinutes;

    public string ConnectionString => $"Data Source={DatabasePath}";
}
