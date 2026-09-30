using CafePos.Core.Data;

namespace CafePos.Core.Schema;

/// <summary>
/// One ordered, idempotent schema step. Implementations must be safe to run on a database
/// that already contains user data (cash register!), therefore every step checks the
/// current schema before touching it and never drops user data silently.
/// </summary>
public interface ISchemaMigration
{
    /// <summary>Monotonic version number.</summary>
    int Version { get; }

    /// <summary>Short name stored in the SchemaVersions table.</summary>
    string Name { get; }

    Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken);
}
