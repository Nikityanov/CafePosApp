using System.Text;

namespace CafePos.Core.Common;

/// <summary>
/// Russian phone numbers as they are typed at the till: normalised for storage, checked, and masked
/// for the customer's screen.
/// </summary>
/// <remarks>
/// <b>NO EXTERNAL LIBRARY, AND THAT IS THE DECISION.</b> libphonenumber would restore a package
/// with its own metadata updates and build machinery for one field in one country. The whole of what
/// this app needs is: strip what people type around a number, put the country code in front, and
/// refuse anything that is not a number. When the café sells outside Russia, this class is where a
/// library arrives — not before, because a general parser brings national numbering plans, and a
/// till that accepts a number it cannot deliver to is worse than one that refuses it.
/// </remarks>
public static class PhoneNumber
{
    /// <summary>Highest count of digits E.164 allows in a number, the '+' aside.</summary>
    public const int MaxDigits = 15;

    /// <summary>
    /// Lowest count this till accepts. Not an E.164 rule — E.164 states a maximum and no minimum —
    /// but this is a Russian café, and a Russian number is <see cref="RussianNumberDigits"/> long, so
    /// every shorter thing that reaches this method is a mistyped one. It errs towards refusing,
    /// because a refused phone is typed again and a wrong phone goes onto a fiscal receipt.
    /// </summary>
    public const int MinDigits = RussianNumberDigits;

    /// <summary>
    /// The Russian country code as a national number is typed: an 8 standing in for the 7.
    /// </summary>
    private const char RussianTrunkPrefix = '8';

    /// <summary>The national trunk digit, which is what makes 9 the way a mobile number is written.</summary>
    private const char RussianNationalPrefix = '9';

    /// <summary>The country code that both of the two forms above become.</summary>
    private const char RussianCountryCode = '7';

    /// <summary>
    /// Digits in a Russian number written out in full: the country code 7 plus ten. Two things key off
    /// it, for two different reasons that happen to be the same number — it is the shortest thing this
    /// till will accept, and it is how "the country code is already written in the digits" is told
    /// apart from "this is an 11-digit national number".
    /// </summary>
    private const int RussianNumberDigits = 11;

    /// <summary>
    /// U+2022 BULLET — not a hyphen and not an asterisk, because the mask has to read as a mask at a
    /// glance and a row of hyphens reads as a row of typos.
    /// </summary>
    private const char Bullet = '•';

    /// <summary>
    /// The storage form of a typed number: E.164, <c>+</c>, country code, digits — or <c>null</c>.
    /// <para>
    /// <b>RETURNS null RATHER THAN THROWING, ON PURPOSE.</b> An absent phone is a normal state, not a
    /// fault: counter service never asks for one, and a customer may decline to give it. An exception
    /// here would turn "the customer did not want to give a number" into an error message, and the
    /// caller would have to catch something in order to do nothing.
    /// </para>
    /// <para>
    /// What is refused is not "not enough digits" but "not a number": a letter or any other character
    /// beyond the punctuation people put around a number, and more than <see cref="MaxDigits"/> digits.
    /// A character that should not be there is a slip of the finger, and storing the slip is how a
    /// contact list fills with numbers nobody can dial. Length alone is <see cref="IsValid"/>'s
    /// question, and keeping the two apart is why a short-but-real foreign number can be looked at by
    /// a person instead of being silently padded or silently dropped here.
    /// </para>
    /// <para>
    /// E.164 is the STORAGE and transmission format; E.123 (spaced, as typed) is the display format.
    /// Normalising now is cheap and normalising later is a migration over every row that already
    /// holds free text, so the conversion happens at the edge, once, on the way in.
    /// </para>
    /// </summary>
    /// <param name="raw">Whatever was typed or pasted: digits, spaces, brackets, dashes, dots, a 7/8/9 in front.</param>
    /// <returns><c>+79XXXXXXXXX</c>, another <c>+</c>-number as typed, or <c>null</c>.</returns>
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

            // The country code already typed with the whole number, "79161234567". Prefixing +7 to it
            // again would store "+779161234567": still 13 digits, so the length check would wave it
            // through, and a number nobody can dial would leave the till on a fiscal receipt. The
            // length is checked instead of trusting the first digit alone, because
            // "+7 701 123 45 67" is also eleven digits and would be mangled the other way round.
            _ when number[0] == RussianCountryCode && number.Length == RussianNumberDigits
                => Bounded("+" + number),

            _ => Bounded("+" + RussianCountryCode + number)
        };
    }

    /// <summary>
    /// Whether a normalised number is one this till is willing to store and to read out to a customer.
    /// <para>
    /// Expects the output of <see cref="Normalize"/> and answers a different question from it: parsing
    /// asks "is this a number", this asks "is this a number we can use". A typed E.123 string returns
    /// false rather than being accepted here — the pair is meant to be used in that order, and quietly
    /// repairing a second time in two places is how two different answers end up in one column.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The display form for the CUSTOMER's screen: <c>+7 ••• ••• •• 42</c>.
    /// <para>
    /// <b>WHY THIS SHAPE.</b> PCI DSS 3.4.1 asks for at least the first six and last four digits of a
    /// PAN to be masked, and it is written about a card number, not a phone number: its figure is a
    /// floor, not a shape, so hiding more than the floor asks is what compliance means here and not
    /// what it costs. The shape kept is the country code, the middle in groups of three, and the last
    /// two digits — 8 of 11 digits hidden on a Russian number, with enough left for the customer to
    /// recognise their own number as they read it back to the cashier.
    /// </para>
    /// <para>
    /// Free text that is not a number (only a '+', only punctuation) comes back as it went in: there is
    /// nothing to hide in it and an operator seeing their own empty input echoed is more useful than a
    /// row of bullets.
    /// </para>
    /// </summary>
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

        // One leading digit and two trailing ones is everything this ever keeps, so a number with
        // three digits or fewer has nothing left to cover and is printed whole — which can only be a
        // value IsValid has already refused.
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

    /// <summary>
    /// Assembles the result and enforces the E.164 ceiling. The ceiling is applied HERE rather than on
    /// the input because every branch changes the count differently — the 8 becomes a 7, a leading 9
    /// becomes a 79 — and a check made once, at the single exit, cannot be forgotten by a new branch.
    /// </summary>
    private static string? Bounded(string assembled)
    {
        var digits = assembled.Length - (assembled[0] == '+' ? 1 : 0);
        return digits > MaxDigits ? null : assembled;
    }
}