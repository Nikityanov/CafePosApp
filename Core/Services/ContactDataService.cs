using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>Erases the phone numbers nobody has a purpose to keep any more.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public sealed class ContactDataService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<ContactDataService> logger) : IContactDataService
{
    /// <summary>How long a phone is kept.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);

    public async Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        if (olderThan <= TimeSpan.Zero) return 0;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var cutoff = timeProvider.GetUtcNow() - olderThan;

        /// <summary>The candidates are read and dated IN MEMORY on purpose, and this is the same reason GetActiveOrdersAsync sorts there: SQLite cannot translate a compar…</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        var candidates = await db.Orders.AsNoTracking()
            .Where(order => order.CustomerPhone != null
                && (order.Status == OrderStatus.Completed || order.Status == OrderStatus.Cancelled))
            .Select(order => new { order.Id, order.CompletedAt, order.CancelledAt })
            .ToListAsync(cancellationToken);

        var expired = candidates
            .Where(order => ClosedAt(order.CompletedAt, order.CancelledAt) < cutoff)
            .Select(order => order.Id)
            .ToList();

        if (expired.Count == 0)
        {
            logger.LogDebug("Contact data purge: nothing older than {Cutoff}", cutoff);
            return 0;
        }

        /// <summary>One statement, and no entities: the whole point of a bulk erase is that the data is not loaded into the process at all.</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        var purged = await db.Orders
            .Where(order => expired.Contains(order.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.CustomerPhone, (string?)null), cancellationToken);

        logger.LogInformation(
            "Contact data purge: cleared {Count} phone numbers from orders closed before {Cutoff}",
            purged, cutoff);

        return purged;
    }

    /// <summary>When the order stopped being live: the completion for a sale, the cancellation for a void.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    private static DateTimeOffset ClosedAt(DateTimeOffset? completedAt, DateTimeOffset? cancelledAt) =>
        completedAt ?? cancelledAt ?? DateTimeOffset.MinValue;
}
