using System.Text;

namespace CafePos.Core.Common;

/// <summary>
/// The key that decides whether a tapped dish joins the line already in the cart or opens a new one.
/// </summary>
/// <remarks>
/// <b>WHY IT IS ONE FUNCTION AND NOT FOUR PREDICATES.</b> The comparison was written inline in four
/// places, and three of them agreed while one did not: the cart, the add command and the order editor
/// all compared product + modifier + variant, but the order editor left the variant out — so adding
/// a large and a small of the same dish merged two lines that should have been separate. A key that
/// every caller computes the same way cannot drift that way again, and a mismatch is a failing test
/// rather than a receipt that quietly loses a line.
/// <para>
/// The component signature is part of the key, and that is what makes two bundles with the same dish
/// but different slots two different lines instead of one line at whichever price was added first.
/// </para>
/// </remarks>
public static class OrderLineKey
{
    /// <summary>
    /// A key for one cart line: product, modifier, variant and, for a bundle, its composition.
    /// </summary>
    /// <param name="productId">The dish the line is for.</param>
    /// <param name="modifier">Chosen modifier name, or <c>null</c> for none.</param>
    /// <param name="variant">Chosen variant name, or <c>null</c> for none.</param>
    /// <param name="components">
    /// The slots of a bundle as (dish, count per unit). <c>null</c> or empty for an ordinary dish —
    /// both mean "not a bundle", and both produce the same key, so an empty list cannot accidentally
    /// split a plain dish from itself.
    /// </param>
    /// <remarks>
    /// The caller is expected to compare keys with <see cref="string.Equals(string?, string?)"/> /
    /// <c>==</c>, which is exactly what the existing merge logic does, so the value is built to be
    /// compared and never parsed. It is not persisted and never shown.
    /// </remarks>
    public static string For(
        Guid productId,
        string? modifier,
        string? variant,
        IEnumerable<(Guid productId, int quantity)>? components = null)
    {
        // Modifier and variant names are free text from the catalogue and may contain any character,
        // including the separator below. Each one is length-prefixed so that "AB" + "" cannot produce
        // the same key as "A" + "B" — which would merge two different dishes at two different prices
        // into one line and charge the quantity sum at one of them.
        var key = new StringBuilder(64);
        key.Append(productId.ToString("N"))
            .Append('|').Append(modifier?.Length ?? 0).Append(':').Append(modifier)
            .Append('|').Append(variant?.Length ?? 0).Append(':').Append(variant);

        if (components is not null)
        {
            // Sorted by ProductId and THEN by quantity, because ProductId alone does not order two
            // slots of the same dish against each other: with equal ids the sort is not required to be
            // stable, so (A,1),(A,2) could fold as "1,2" on one tap and "2,1" on the next, and two
            // identical bundles would sit in two lines. The tie-break costs nothing and makes the
            // fold total.
            //
            // The pairs are NOT added together: two slots of the same dish are two slots, and the sum
            // they contribute is the same either way (2 + 1 is 1 + 2 is 3), so merging them would buy
            // nothing while hiding which composition was actually sold.
            var signature = components
                .OrderBy(component => component.productId)
                .ThenBy(component => component.quantity)
                .Select(component => $"{component.productId.ToString("N")}x{component.quantity}")
                .ToList();

            if (signature.Count > 0) key.Append("|+").Append(string.Join(',', signature));
        }

        return key.ToString();
    }
}