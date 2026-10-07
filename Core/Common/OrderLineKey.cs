using System.Text;

namespace CafePos.Core.Common;

/// <summary>The key that decides whether a tapped dish joins the line already in the cart or opens a new one.</summary>
/// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

public static class OrderLineKey
{
    /// <summary>A key for one cart line: product, modifier, variant and, for a bundle, its composition.</summary>
    /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

    public static string For(
        Guid productId,
        string? modifier,
        string? variant,
        IEnumerable<(Guid productId, int quantity)>? components = null)
    {
        /// <summary>Modifier and variant names are free text from the catalogue and may contain any character, including the separator below.</summary>
        /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

        var key = new StringBuilder(64);
        key.Append(productId.ToString("N"))
            .Append('|').Append(modifier?.Length ?? 0).Append(':').Append(modifier)
            .Append('|').Append(variant?.Length ?? 0).Append(':').Append(variant);

        if (components is not null)
        {
            /// <summary>Sorted by ProductId and THEN by quantity, because ProductId alone does not order two slots of the same dish against each other: with equal ids the sor…</summary>
            /// <remarks>Почему так — `docs/decisions/orders.md`</remarks>

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
