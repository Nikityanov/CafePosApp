using System.Text;

namespace CafePos.Core.Common;

/// <summary>Russian phone numbers as they are typed at the till: normalised for storage, checked, and masked for the customer's screen.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public static class PhoneNumber
{
    /// <summary>Highest count of digits E.164 allows in a number, the '+' aside.</summary>
    public const int MaxDigits = 15;

    /// <summary>Lowest count this till accepts.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public const int MinDigits = RussianNumberDigits;

    /// <summary>
    /// The Russian country code as a national number is typed: an 8 standing in for the 7.
    /// </summary>
    private const char RussianTrunkPrefix = '8';

    /// <summary>The national trunk digit, which is what makes 9 the way a mobile number is written.</summary>
    private const char RussianNationalPrefix = '9';

    /// <summary>The country code that both of the two forms above become.</summary>
    private const char RussianCountryCode = '7';

    /// <summary>Digits in a Russian number written out in full: the country code 7 plus ten.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    private const int RussianNumberDigits = 11;

    /// <summary>U+2022 BULLET — not a hyphen and not an asterisk, because the mask has to read as a mask at a glance and a row of hyphens reads as a row of typos.</summary>

    private const char Bullet = '•';

    /// <summary>The storage form of a typed number: E.164, `+`, country code, digits — or `null`.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new StringBuilder(raw.Length);
        var hasCountryCodeMark = false;

        foreach (var character in raw)
        {
            if (character is >= '0' and <= '9')
            {
                digits.Append(character);
                continue;
            }

            if (character == '+')
            {
                // Only ever as the first thing that is not punctuation, and only once: a second '+',
                // or one after a digit, means the text is not a number that anybody dialled.
                if (hasCountryCodeMark || digits.Length > 0) return null;
                hasCountryCodeMark = true;
                continue;
            }

            // The punctuation a number actually gets typed with. Whitespace is the wide kind, because
            // a number copied out of a contact card arrives with a non-breaking space in it.
            if (char.IsWhiteSpace(character) || character is '(' or ')' or '-' or '.') continue;

            // Anything else — a letter, a slash, an emoji from a chat — is a slip, not a number.
            return null;
        }

        if (digits.Length == 0) return null;

        // Materialised once so the slice in the Russian branches below is a string slice: a
        // StringBuilder has an indexer but no Range indexer.
        var number = digits.ToString();

        if (hasCountryCodeMark) return Bounded("+" + number);

        // How a Russian number is written when no country code is written.
        return number[0] switch
        {
            // "8 (916) 123-45-67" — the 8 is the long-distance trunk prefix and stands for the 7.
            RussianTrunkPrefix => Bounded("+" + RussianCountryCode + number[1..]),

            // "916 123-45-67" — a mobile number starts with the 9, which is not part of the number.
            RussianNationalPrefix => Bounded("+" + RussianCountryCode + RussianNationalPrefix + number[1..]),

            // The country code already typed with the whole number, "79161234567".
            // Почему так — `docs/decisions/shared.md`

            _ when number[0] == RussianCountryCode && number.Length == RussianNumberDigits
                => Bounded("+" + number),

            _ => Bounded("+" + RussianCountryCode + number)
        };
    }

    /// <summary>Whether a normalised number is one this till is willing to store and to read out to a customer.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static bool IsValid(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (normalized[0] != '+') return false;

        var digits = normalized.AsSpan(1);
        if (digits.Length is < MinDigits or > MaxDigits) return false;

        foreach (var character in digits)
        {
            if (character is < '0' or > '9') return false;
        }

        return true;
    }

    /// <summary>The display form for the CUSTOMER's screen: `+7 ••• ••• •• 42`.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string Mask(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var hasPlus = normalized[0] == '+';
        var collected = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is >= '0' and <= '9') collected.Append(character);
        }

        var digits = collected.ToString();

        /// <summary>One leading digit and two trailing ones is everything this ever keeps, so a number with three digits or fewer has nothing left to cover and is printed whole — which can only be a value IsValid has already refused.</summary>

        if (digits.Length <= 3) return normalized;

        var maskedCount = digits.Length - 3;
        var masked = new StringBuilder(maskedCount * 4);
        for (var index = 0; index < maskedCount; index++)
        {
            if (index > 0 && index % 3 == 0) masked.Append(' ');
            masked.Append(Bullet);
        }

        return string.Concat(
            hasPlus ? "+" : string.Empty,
            digits[0],
            " ",
            masked,
            " ",
            digits[^2],
            digits[^1]);
    }

    /// <summary>Assembles the result and enforces the E.164 ceiling.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    private static string? Bounded(string assembled)
    {
        var digits = assembled.Length - (assembled[0] == '+' ? 1 : 0);
        return digits > MaxDigits ? null : assembled;
    }
}
