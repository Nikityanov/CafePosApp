namespace CafePos.Core.Models;

/// <summary>What the customer is doing with the order: eating it on the premises, or taking it away.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public enum OrderType
{
    /// <summary>Served on the premises. No phone is asked for and none is stored; the promise is CreatedAt + LeadTimeMinutes.</summary>

    CounterService,

    /// <summary>The customer takes it away. This is the only type for which a phone is needed to fulfil the order, and therefore the only one that keeps .</summary>

    Takeaway
}
