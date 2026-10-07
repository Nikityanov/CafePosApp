namespace CafePos.Core.Data;

/// <summary>Applied database schema version. Replaces the previous mixture of EnsureCreated() + ad-hoc ALTER TABLE calls and the EF migrations that were never applied (and referenced already deleted models).</summary>

public class SchemaVersion
{
    /// <summary>Monotonic migration number (primary key).</summary>
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}
