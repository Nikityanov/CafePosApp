using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>
/// A dish a bundle slot may be filled with, as the composition sheet offers it.
/// </summary>
/// <remarks>
/// MOVED HERE FROM <c>CafePos.Presentation/Services/Abstractions/PlatformServices.cs</c>, beside
/// <see cref="IComboEditor"/> which still lives there. The record says nothing about how it reaches
/// the screen — it is a fact about a bundle — and it was untestable from the Tests project while it
/// sat in a file about sheets and dialogs.
/// </remarks>
/// <param name="ProductId">
/// The SLOT's dish, never the substitute's. <c>ResolveOne</c> matches an incoming component to a
/// catalogue slot by the slot's <c>ProductId</c> and refuses anything it cannot match, so a cart
/// carrying the substitute's identifier would be refused as "a dish that is no longer part of this
/// bundle". Only the label and the two prices describe the substitute.
/// </param>
/// <param name="Label">
/// What the sheet prints. Pre-composed by the caller rather than assembled here, because the only case
/// that needs composing is a substitution - the slot's dish ran out and this is the replacement - and
/// the words for that belong next to the rule that decides it. See <see cref="BundlePlan"/>.
/// </param>
/// <param name="UnitKopecks">
/// What one of this dish is charged inside the bundle, in kopecks, already resolved by
/// <see cref="ComboPricing.ResolveUnitKopecks"/>: a slot priced explicitly keeps that
/// price, otherwise the dish's own price is used. The sheet shows a running total from these, and the
/// price that is actually charged is recomputed from the catalogue at checkout.
/// </param>
/// <param name="ReferenceKopecks">
/// What this dish costs on its own, in kopecks - Simphony's "Prep Cost", and the figure
/// <see cref="OrderItemComponent.ReferencePriceKopecks"/> keeps beside
/// <c>UnitPriceKopecks</c> so that one sale can answer two reports. The sheet shows what the bundle
/// saves against it.
/// </param>
public sealed record ComboSlotOption(Guid ProductId, string Label, long UnitKopecks, long ReferenceKopecks);

/// <summary>One slot of a bundle as the cashier left it: which dish, and how many of it per unit.</summary>
public sealed record ComboSlotChoice(Guid ProductId, int QuantityPerUnit);

/// <summary>
/// What the composition sheet opens on: the dishes it may offer, what is in the sheet already, and
/// the bundle's own price.
/// </summary>
/// <remarks>
/// ONE REQUEST SHAPE FOR BOTH CASES, and that is the whole point: a catalogue bundle arrives with a
/// filled <see cref="Selection"/> and <see cref="Empty"/> arrives with none. They are the
/// same sheet, the same rows and the same confirm — a custom build is not a second mechanic, it is
/// this one started empty.
/// </remarks>
/// <param name="Title">Sheet title, naming the bundle.</param>
/// <param name="Options">The dishes a slot may hold. See <see cref="ComboSlotOption"/>.</param>
/// <param name="Selection">The slots already in the bundle; empty for a custom build.</param>
/// <param name="PriceKopecks">
/// The bundle's own price, in kopecks — what the till charges. Shown as the sheet's main figure,
/// with the à la carte sum of the slots as the reference it is measured against. Zero when the
/// caller does not know it (a custom build from scratch), in which case the sheet shows the sum
/// alone.
/// </param>
public sealed record ComboEditorRequest(
    string Title,
    IReadOnlyList<ComboSlotOption> Options,
    IReadOnlyList<ComboSlotChoice> Selection,
    long PriceKopecks = 0)
{
    /// <summary>An empty build: the sheet with nothing chosen yet.</summary>
    public static ComboEditorRequest Empty { get; } = new("Состав комбо", [], []);
}