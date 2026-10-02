using CafePos.Core.Models;

namespace CafePosApp.Services;

/// <summary>
/// Which of the two dispositions the operator picked, boxed so the popup can return one.
/// </summary>
/// <remarks>
/// A class rather than a bare <see cref="StockDisposition"/> because the popup has to be able to
/// say "nothing was picked". Returning the enum directly would make a dismissal indistinguishable
/// from a deliberate "leave written off", and the second of those is a decision that changes the
/// stock ledger.
/// </remarks>
public sealed record StockDispositionChoice(StockDisposition Value);

/// <summary>What the disposition sheet is opened for.</summary>
public sealed record StockDispositionSheetRequest(
    string OrderTitle,
    decimal Total,
    decimal Paid,
    StockDisposition? Current = null);

/// <summary>
/// Asks what happens to the ingredients of a cancelled order. Implemented by a bottom sheet.
/// </summary>
/// <remarks>
/// Returns <c>null</c> when the sheet is dismissed, which the caller must treat as "do not cancel
/// at all": the disposition is not an optional detail of a cancellation, it is half of it.
/// <para>
/// This exists as its own surface rather than as an <see cref="IDialogService.ChooseAsync"/> action
/// sheet because the choice cannot be made from two option labels. The two answers look
/// interchangeable until you know why the second one is on the screen at all, and the operator has
/// to know that to pick correctly.
/// </para>
/// </remarks>
public interface IStockDispositionSheet
{
    Task<StockDisposition?> ChooseAsync(StockDispositionSheetRequest request, CancellationToken cancellationToken = default);
}