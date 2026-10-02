namespace CafePos.Core.Models;

/// <summary>
/// How money was received. Persisted as TEXT (see ConfigureOrders), and deliberately a closed
/// set: a POS has exactly two tills (the drawer and the terminal), so a new method is a code plus
/// migration change, never a free-text entry an operator could typo into the ledger.
/// </summary>
public enum PaymentMethod
{
    /// <summary>Cash in the drawer — the only method that can be checked against a till count.</summary>
    Cash,
    Card
}
