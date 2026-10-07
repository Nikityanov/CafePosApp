using CafePos.Core.Data;

namespace CafePos.Core.Schema;

/// <summary>One ordered, idempotent schema step.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface ISchemaMigration
{
    // Monotonic version number.
    int Version { get; }

    // Short name stored in the SchemaVersions table.
    string Name { get; }

    Task ApplyAsync(AppDbContext db, CancellationToken cancellationToken);
}
