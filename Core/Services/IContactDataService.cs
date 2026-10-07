using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>Retention of customer contact data: 152-ФЗ ст.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IContactDataService
{
    /// <summary>Clears Order.CustomerPhone on orders that are closed and were closed before ago. Returns how many orders were changed.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}
