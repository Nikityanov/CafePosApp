using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>The one rule for turning a typed phone number into a stored one. Shared by every path that can write .</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

internal static class ContactPhoneRule
{
    /// <summary>Normalises a typed number for storage, or returns `null` when the order may not hold one.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public static string? ForStorage(string? typed, OrderType orderType)
    {
        if (orderType != OrderType.Takeaway) return null;
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var normalized = PhoneNumber.Normalize(typed);
        if (normalized is null)
            throw new ValidationFailureException(
                "Телефон не похож на номер: проверьте раскладку и лишние символы. Номер нужен, чтобы клиент дождался заказа.");

        /// <summary>A number that parsed but is not a usable length is refused rather than stored: a fiscal receipt with "+71234" on it is a worse outcome than an order t…</summary>
        /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

        if (!PhoneNumber.IsValid(normalized))
            throw new ValidationFailureException(
                $"Телефон {normalized} — это не полный номер. Для выдачи заказа нужен номер целиком, например +7 916 123-45-67.");

        return normalized;
    }
}
