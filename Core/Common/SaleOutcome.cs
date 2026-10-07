namespace CafePos.Core.Common;

/// <summary>How a sale attempt ended.</summary>
public enum SaleOutcome
{
    /// <summary>The operator dismissed the payment sheet, or pressed back. Nothing was attempted.</summary>
    Dismissed,

    /// <summary>The order was booked. The only outcome that changes the cart.</summary>
    Booked,

    /// <summary>
    /// The domain refused the sale - out of stock, a price that moved, or anything else it threw -
    /// and the cart is still exactly what the operator built.
    /// </summary>
    Refused,

    /// <summary>There is no open shift and the operator asked to open one.</summary>
    OfferShift
}

/// <summary>
/// What a sale attempt produced: how it ended, and the order when it ended by booking one.
/// </summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="OrderNumber">The booked order's number, when booked.</param>
/// <param name="TotalPrice">What the order was booked for.</param>
/// <param name="ReceiptText">
/// The money sentence for the operator - which method, or that it is unpaid. It comes from
/// <c>PaymentBooking</c>, so the order and the message cannot disagree about what happened.
/// </param>
public sealed record SaleResult(
    SaleOutcome Outcome,
    int? OrderNumber = null,
    decimal TotalPrice = 0m,
    string? ReceiptText = null)
{
    public static SaleResult Dismissed { get; } = new(SaleOutcome.Dismissed);

    public static SaleResult Refused { get; } = new(SaleOutcome.Refused);

    public static SaleResult OfferShift { get; } = new(SaleOutcome.OfferShift);

    public bool IsBooked => Outcome == SaleOutcome.Booked;
}