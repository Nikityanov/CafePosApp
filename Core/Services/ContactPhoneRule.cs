using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>
/// The one rule for turning a typed phone number into a stored one. Shared by every path that can write
/// <see cref="Order.CustomerPhone"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS IS A SEPARATE TYPE AND NOT A SECOND CALL SITE.</b> The rule is not "parse a phone": it is
/// three decisions in a fixed order — is this order allowed to hold a number at all, is what was typed a
/// number, and is that number one this till will store. That sequence existed as a private method inside
/// <c>CheckoutService</c>, which made it unreachable from the other door into the same column: adding a
/// phone to an order after it was paid for. Copying it there would have produced two implementations of
/// one legal contour, and the copy is exactly the one nobody writes a test for.
/// </para>
/// <para>
/// <b>THE ORDER OF THE CHECKS IS THE POINT.</b> Fulfilment mode is tested FIRST, before anything is
/// parsed. 152-ФЗ ст. 6(1)(5) allows a phone only where it is needed for the contract, and a
/// counter-service customer is on the premises, so the number is discarded without being read. A till
/// that validates text it is about to throw away stops a sale for nothing, which is why
/// <c>CheckoutService</c>'s own comment records that choice too.
/// </para>
/// </remarks>
internal static class ContactPhoneRule
{
    /// <summary>
    /// Normalises a typed number for storage, or returns <c>null</c> when the order may not hold one.
    /// </summary>
    /// <param name="typed">Whatever was typed or pasted.</param>
    /// <param name="orderType">
    /// The fulfilment mode the number will be stored under. A counter-service order yields <c>null</c>
    /// regardless of what was typed.
    /// </param>
    /// <exception cref="ValidationFailureException">
    /// The text is not a number, or is a number this till will not store. Never thrown for counter
    /// service, because that number is being discarded rather than stored.
    /// </exception>
    public static string? ForStorage(string? typed, OrderType orderType)
    {
        if (orderType != OrderType.Takeaway) return null;
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var normalized = PhoneNumber.Normalize(typed);
        if (normalized is null)
            throw new ValidationFailureException(
                "Телефон не похож на номер: проверьте раскладку и лишние символы. Номер нужен, чтобы клиент дождался заказа.");

        // A number that parsed but is not a usable length is refused rather than stored: a fiscal
        // receipt with "+71234" on it is a worse outcome than an order that could not be taken, and the
        // message says what is wrong instead of leaving the operator to guess.
        if (!PhoneNumber.IsValid(normalized))
            throw new ValidationFailureException(
                $"Телефон {normalized} — это не полный номер. Для выдачи заказа нужен номер целиком, например +7 916 123-45-67.");

        return normalized;
    }
}
