using CafePos.Core.Models;

namespace CafePos.Presentation.Services;

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
