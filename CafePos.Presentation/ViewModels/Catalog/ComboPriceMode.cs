using CafePos.Core.Models;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// What one slot of a bundle is charged inside it — the three states of
/// <see cref="ComboComponent.ComponentPriceKopecks"/>, named rather than left as a nullable number.
/// </summary>
/// <remarks>
/// The three states are genuinely three different decisions, not one number with three spellings:
/// <see cref="DishPrice"/> is <c>null</c> (the dish's own price — the normal case), <see cref="Free"/>
/// is <c>0</c> ("included, no charge") and <see cref="Fixed"/> is an amount. Collapsing them into
/// "price" and leaving 0 to mean either "free" or "mistyped zero" is how a bundle ends up quietly
/// giving something away, so the form asks the question by name and the model keeps the three apart.
/// </remarks>
public enum ComboPriceMode
{
    /// <summary>The dish's own price (<c>ComponentPriceKopecks == null</c>).</summary>
    DishPrice,

    /// <summary>Included in the bundle and charged nothing (<c>0</c>).</summary>
    Free,

    /// <summary>Charged this instead of the dish's price (Simphony's "Add Side Prices").</summary>
    Fixed
}
