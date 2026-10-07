namespace CafePos.Core.Models;

/// <summary>How money was received.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public enum PaymentMethod
{
    /// <summary>Cash in the drawer — the only method that can be checked against a till count.</summary>
    Cash,
    Card
}
