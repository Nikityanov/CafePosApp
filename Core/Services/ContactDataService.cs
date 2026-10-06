using CafePos.Core.Data;
using CafePos.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CafePos.Core.Services;

/// <summary>
/// Erases the phone numbers nobody has a purpose to keep any more.
/// </summary>
/// <remarks>
/// <b>ERASE THE VALUE, NEVER THE ROW.</b> 152-ФЗ ст. 5(7) is about the personal data, not about the
/// sale: the receipt is a fiscal document and deleting orders to satisfy a retention rule would be
/// trading one legal problem for a worse one. So the column is nulled in place and the order — its
/// numbers, its lines, its payment — is untouched. What leaves the database is exactly the thing that
/// identifies a person.
/// <para>
/// <b>IT IS THE NUMBER OF ROWS THAT COUNTS, WHICH IS WHY THE WRITE IS BULK.</b> CoAP 13.11 ч.12 prices
/// unlawful disclosure at 3–5 million ₽ for 1 000–10 000 subjects and counts rows, so a hundred phones
/// cleared one at a time over a year is a hundred rows an inspection could have found. The statement
/// therefore updates every eligible row in one pass, inside the database, instead of loading entities
/// and saving them one by one.
/// </para>
/// </remarks>
public sealed class ContactDataService(
    IDbContextFactory<AppDbContext> factory,
    TimeProvider timeProvider,
    ILogger<ContactDataService> logger) : IContactDataService
{
    /// <summary>
    /// How long a phone is kept. Ninety days is long enough to settle a complaint about a takeaway
    /// ("nobody called me back") and short enough that the data is gone long before anyone asks about
    /// it. It is a default and not a hard-coded policy: the shift close passes it in, and the interface
    /// takes any threshold, so a café with its own retention period can say so without this changing.
    /// </summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(90);

    public async Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        if (olderThan <= TimeSpan.Zero) return 0;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var cutoff = timeProvider.GetUtcNow() - olderThan;

        // The candidates are read and dated IN MEMORY on purpose, and this is the same reason
        // GetActiveOrdersAsync sorts there: SQLite cannot translate a comparison or an ORDER BY over a
        // DateTimeOffset and throws NotSupportedException rather than doing something approximate. The
        // set is small by construction — it is only the closed orders that still HAVE a phone, and
        // this service shrinks it once a day — so the projection costs less than any workaround that
        // would put a timestamp comparison back into SQL.
        //
        // Closed AND carrying a number worth erasing, in the query rather than after it: the sweep
        // then reads and rewrites only the rows there is work to do. An order still being cooked keeps
        // its number — the customer has not been served yet, so the purpose has not ended.
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

        // One statement, and no entities: the whole point of a bulk erase is that the data is not
        // loaded into the process at all. ExecuteUpdate bypasses the change tracker, which is also what
        // keeps this cheap on a database that lives in a phone's flash.
        var purged = await db.Orders
            .Where(order => expired.Contains(order.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.CustomerPhone, (string?)null), cancellationToken);

        logger.LogInformation(
            "Contact data purge: cleared {Count} phone numbers from orders closed before {Cutoff}",
            purged, cutoff);

        return purged;
    }

    /// <summary>
    /// When the order stopped being live: the completion for a sale, the cancellation for a void. A
    /// row with neither is a closed order whose terminal never wrote the timestamp — the cancellation
    /// path always writes CancelledAt and the status path always writes CompletedAt, so this is a
    /// hand-edited or interrupted row, and falling back to the oldest possible moment errs towards
    /// purging a number rather than towards keeping one forever.
    /// </summary>
    private static DateTimeOffset ClosedAt(DateTimeOffset? completedAt, DateTimeOffset? cancelledAt) =>
        completedAt ?? cancelledAt ?? DateTimeOffset.MinValue;
}
