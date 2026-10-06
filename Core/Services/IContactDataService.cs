using Microsoft.EntityFrameworkCore;

namespace CafePos.Core.Services;

/// <summary>
/// Retention of customer contact data: 152-ФЗ ст. 5(7) requires personal data to be destroyed once
/// the purpose for collecting it is met, and a phone number on a takeaway order has had its purpose
/// the moment the order is handed over.
/// </summary>
public interface IContactDataService
{
    /// <summary>
    /// Clears <c>Order.CustomerPhone</c> on orders that are closed and were closed before
    /// <paramref name="olderThan"/> ago. Returns how many orders were changed.
    /// </summary>
    /// <remarks>
    /// Called from the shift close, which is the schedule this app already has. A nightly job or a
    /// background timer would be a second thing that has to run, has to be rescheduled when the phone
    /// restarts, and can be missed silently — and a retention promise that depends on a scheduler
    /// firing is not a promise. Once a day is also the coarsest interval that could honestly satisfy
    /// 90 days, and it comes free with something an operator already does at the end of every working
    /// day.
    /// <para>
    /// ONLY CLOSED ORDERS. A phone on an order still being cooked is still the only way to reach the
    /// customer about it, and the purpose has not ended yet. Clearing it would be the wrong kind of
    /// thorough.
    /// </para>
    /// </remarks>
    Task<int> PurgeAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}