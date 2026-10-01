namespace CafePos.Core.Models;

/// <summary>
/// Payment state of an order. Derived, never persisted, and deliberately NOT called PaymentStatus:
/// in this codebase a *Status name means a stored column (Order.Status is the string
/// "InProgress"/"Ready"/...). A [NotMapped] PaymentStatus would be read as "just not saved yet"
/// and would eventually be "fixed" with a column that can then disagree with the ledger — the same
/// class of bug as deriving money from a collection instead of a scalar.
/// </summary>
public enum PaymentState
{
    Unpaid,
    PartiallyPaid,
    Paid
}
