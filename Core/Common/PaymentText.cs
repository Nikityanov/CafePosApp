using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>
/// Shared Russian wording for payment methods. Used in the checkout confirmation, the orders
/// board and the order details, so "наличными" / "картой" is spelled one way across the app.
/// </summary>
public static class PaymentText
{
    /// <summary>"наличными" for cash, "картой" for card — the instrumental case that follows "оплачено".</summary>
    public static string Method(PaymentMethod method) =>
        method == PaymentMethod.Cash ? "наличными" : "картой";
}

// MOVED HERE from CafePos.Presentation/Services/PaymentText.cs. It is a wording table with no UI in it,
// and PaymentBooking - the rule about what an order records about money - needs it from Core.
