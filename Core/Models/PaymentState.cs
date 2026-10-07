namespace CafePos.Core.Models;

/// <summary>Payment state of an order.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public enum PaymentState
{
    Unpaid,
    PartiallyPaid,
    Paid
}
