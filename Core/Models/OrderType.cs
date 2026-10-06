namespace CafePos.Core.Models;

/// <summary>
/// What the customer is doing with the order: eating it on the premises, or taking it away.
/// </summary>
/// <remarks>
/// WHY AN ENUM AND NOT A BOOLEAN. A "isTakeaway" flag would be one more independent truth that the
/// phone question then has to be reconciled against, and the two could disagree — an order with a
/// phone and no takeaway, which is exactly the row 152-ФЗ ст. 5(4)-(5) calls redundant. Tying the
/// contact to the fulfilment mode instead of to the typing of a number is what makes "we do not
/// store it" a property of the order rather than a promise: CounterService stores no phone at all,
/// which is the less intrusive alternative EDPB Guidelines 2/2019 п. 25 says makes the processing
/// unnecessary in the first place.
/// <para>
/// There is no "Other" and no "Delivery": every member has to be one an operator can act on at the
/// till, and a value this build does not know must read as itself rather than quietly become the
/// least intrusive member.
/// </para>
/// <para>
/// Persisted as TEXT, like <see cref="OrderStatus"/> — readable straight out of SQLite during an
/// investigation instead of being an ordinal only this build understands. The member names are
/// therefore part of the stored data and are never renamed.
/// </para>
/// </remarks>
public enum OrderType
{
    /// <summary>
    /// Served on the premises. No phone is asked for and none is stored; the promise is
    /// <c>CreatedAt + LeadTimeMinutes</c>.
    /// </summary>
    CounterService,

    /// <summary>
    /// The customer takes it away. This is the only type for which a phone is needed to fulfil the
    /// order, and therefore the only one that keeps <see cref="Order.CustomerPhone"/>.
    /// </summary>
    Takeaway
}