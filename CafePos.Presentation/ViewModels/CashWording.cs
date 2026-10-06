using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The Russian sentences for a cash difference, in one place because two screens say the same
/// thing about the same number: the shift report's post-close message and the analytics card.
/// </summary>
/// <remarks>
/// It lives here, in the presentation layer, and NOT in <c>CafePos.Core</c>, on purpose. Core owns
/// the arithmetic and the <see cref="CashDifference"/> enum and deliberately stops there: "Shortage"
/// is a fact, "Не хватает 240,00 ₽" is a Russian sentence, and a domain that starts shipping UI
/// copy stops being a domain.
/// <para>
/// THE SIGN NEVER APPEARS ALONE. <see cref="Describe"/> takes a signed kopeck value and prints its
/// ABSOLUTE amount, because a bare "-240,00" on its own is ambiguous in Russian: read as a delta it
/// means a shortage, read as a balance it means an overdraft, and the two are opposites for the
/// person who has to decide who lost the money. The direction lives in the word — «Не хватает» and
/// «Лишние» — which is also what makes the three states readable in greyscale and to a screen
/// reader, so nothing here depends on the tint the XAML puts on the label.
/// </para>
/// </remarks>
internal static class CashWording
{
    /// <summary>
    /// "Сходится" / "Не хватает 240,00 ₽" / "Лишние 240,00 ₽" for a signed
    /// <c>CashReconciliation.DiscrepancyKopecks</c>.
    /// </summary>
    /// <param name="discrepancyKopecks">
    /// Counted minus expected. Negative is a shortage, positive an overage, zero is a match.
    /// </param>
    public static string Describe(long discrepancyKopecks) => discrepancyKopecks switch
    {
        0 => "Сходится",
        < 0 => $"Не хватает {TextFormat.Money(Money.FromKopecks(-discrepancyKopecks))}",
        _ => $"Лишние {TextFormat.Money(Money.FromKopecks(discrepancyKopecks))}"
    };
}
