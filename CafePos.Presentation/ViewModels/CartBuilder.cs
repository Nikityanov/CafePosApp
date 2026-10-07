using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The cart: its lines, its total, and the one edit on this screen that can be taken back.
/// </summary>
/// <remarks>
/// <para>
/// One of the six Collaborators the plan named, and the only one of them that needs almost no
/// dependencies. It owns <see cref="Cart"/>, <see cref="Total"/> and the undo, which is what makes
/// <c>MenuViewModel</c> able to stop owning them.
/// </para>
/// <para>
/// <b>WHAT THE SHELL KEEPS AND WHY.</b> <c>Cart</c> and <c>Total</c> moved here. <c>PayAndCreateText</c>
/// and <c>CanCreateOrder</c> did not: both quote the total inside a control that also depends on
/// <c>IsBusy</c>, which is the shell's, so they stay where they can see both. The shell subscribes to
/// <see cref="PropertyChanged"/> and re-announces its own two properties when <see cref="Total"/> moves
/// — otherwise the checkout button would keep quoting the amount the cart had before.
/// </para>
/// <para>
/// <b>THE UNDO HAS NO TIMER, AND THAT IS THE DESIGN.</b> It lives in the message strip that was already
/// there, so it costs no additional row, and it is retired by the NEXT message rather than by a clock.
/// Nothing has to be torn down on a timer, so nothing leaks if the page is left.
/// </para>
/// </remarks>
public sealed partial class CartBuilder : ObservableObject
{
    private decimal total;

    /// <summary>The cart's lines, in the order the operator read them.</summary>
    public ObservableCollection<CartItemViewModel> Cart { get; } = [];

    /// <summary>
    /// The cart total. Setting it raises <see cref="TotalText"/> as well, because the checkout button
    /// binds the formatted amount inside its own caption.
    /// </summary>
    public decimal Total
    {
        get => total;
        private set
        {
            if (SetProperty(ref total, value)) OnPropertyChanged(nameof(TotalText));
        }
    }

    /// <summary>The cart total in the active currency, e.g. «740,00 ₿».</summary>
    public string TotalText => TextFormat.Money(Total);

    /// <summary>Whether the message strip is currently offering to put a line back.</summary>
    public bool HasUndo => pendingUndo is not null;

    private PendingUndo? pendingUndo;

    /// <summary>A removed line, and where it was.</summary>
    private readonly record struct PendingUndo(CartItemViewModel Item, int Index, int Quantity);

    /// <summary>
    /// Puts a line on the cart, or folds it into the identical line already there.
    /// </summary>
    /// <remarks>
    /// The merge is on <see cref="CartItemViewModel.MergeKey"/>, the same function the order editor and
    /// <c>OrderService</c> compute. An inline comparison is what this used to do, and it left the
    /// composition out of the identity — which merged two DIFFERENT builds of one bundle into one line
    /// and put one bundle in as two. Both cost money.
    /// </remarks>
    public void Add(CartItemViewModel line)
    {
        // Adding after removing is the operator moving on, so the pending undo is retired. It would
        // also die on the next message; this makes it immediate rather than merely eventual.
        ClearPendingUndo();

        var existing = Cart.FirstOrDefault(item => item.MergeKey == line.MergeKey);
        if (existing is null) Cart.Add(line);
        else existing.Quantity += line.Quantity;

        Recalculate();
    }

    /// <summary>One step up on an existing line.</summary>
    public void StepUp(CartItemViewModel? item)
    {
        if (item is null) return;
        ClearPendingUndo();
        item.Quantity++;
        Recalculate();
    }

    /// <summary>One step down, and the line away entirely at zero.</summary>
    /// <remarks>
    /// The decision is <see cref="CartStepDown"/>'s — including that only a step which REMOVES the line
    /// arms an undo, and the quantity the undo restores, which has to be the one from BEFORE the
    /// decrement. This method applies it and arms what comes back.
    /// </remarks>
    public CartStepDown? StepDown(CartItemViewModel? item, Action<string> announce)
    {
        if (item is null) return null;

        var step = CartStepDown.From(item.Quantity, item.ProductName);
        var index = step.RemovesLine ? Cart.IndexOf(item) : -1;

        item.Quantity = step.QuantityAfter;

        if (step.RemovesLine)
        {
            // ANNOUNCE FIRST, THEN ARM. The shell's message setter retires the previous undo, so
            // arming first means this line's own undo is cancelled by its own message and the undo
            // control never appears. That is not a theoretical hazard: inverting these two lines is
            // what a move of this code did once, and the characterisation test caught it.
            announce(step.Announce!);

            pendingUndo = new PendingUndo(item, index, step.UndoQuantity!.Value);
            OnPropertyChanged(nameof(HasUndo));
            Cart.Remove(item);
        }

        Recalculate();
        return step;
    }

    /// <summary>
    /// Takes the line the last removal took away, and puts it back where it was.
    /// </summary>
    /// <remarks>
    /// <b>THE QUANTITY HAS TO BE PUT BACK TOO, AND THIS IS NOT A DETAIL.</b> <see cref="StepDown"/>
    /// decrements the line before removing it, so the instance held here arrives with 0 on it.
    /// Re-inserting it as it stands puts a row reading «0» and «Итого 0,00», and the checkout then
    /// fails in the domain with «У каждой позиции заказа должно быть положительное количество» — which
    /// is exactly what the emulator showed the first time this ran. Restoring means «put the line back
    /// AS IT WAS», not merely «put the object back».
    /// </remarks>
    public string? Restore()
    {
        if (pendingUndo is not { } pending) return null;

        // Disarmed FIRST, because the confirmation goes through Message and its setter clears the
        // pending undo anyway — arming nothing and leaving that to the message would work, but only by
        // accident of ordering.
        ClearPendingUndo();

        pending.Item.Quantity = pending.Quantity;

        // The index can be out of range if the cart changed underneath (a draft restored, a line added
        // and removed again); appending is the honest fallback rather than throwing from a tap.
        var at = CartStepDown.RestoreIndex(pending.Index, Cart.Count);
        if (at >= 0) Cart.Insert(at, pending.Item);
        else Cart.Add(pending.Item);

        Recalculate();
        return pending.Item.ProductName;
    }

    /// <summary>Retires a pending undo, as any message does.</summary>
    public void ClearPendingUndo()
    {
        if (pendingUndo is null) return;
        pendingUndo = null;
        OnPropertyChanged(nameof(HasUndo));
    }

    /// <summary>Recomputes the total from the lines. Cheap, and called after every mutation.</summary>
    public void Recalculate() => Total = Cart.Sum(item => item.LineTotal);

    /// <summary>
    /// Re-raises each row's formatted amount, after the selected currency may have changed.
    /// </summary>
    /// <remarks>
    /// Through each row's own notification rather than by rebuilding the collection, so the cart does
    /// not flicker or lose its scroll position when nothing about the money itself changed.
    /// </remarks>
    public void RefreshMoneyText()
    {
        foreach (var item in Cart) item.RefreshMoneyText();
    }

    /// <summary>The cart as checkout and the parking lot want it: one line each.</summary>
    public IReadOnlyList<CheckoutLine> ToCheckoutLines() => [.. Cart.Select(item => item.ToCheckoutLine())];

    /// <summary>
    /// Replaces the cart wholesale — the draft-restore path.
    /// </summary>
    /// <remarks>
    /// Through <c>WithComponents</c> on the caller's side so a restored bundle keeps its slots. A combo
    /// that arrives without them prints as one line with nothing under it AND loses its merge
    /// signature, so it would merge with an identical bundle and split from itself.
    /// </remarks>
    public void RestoreLines(IEnumerable<CartItemViewModel> lines)
    {
        Cart.Clear();
        foreach (var line in lines) Cart.Add(line);
        Recalculate();
    }
}