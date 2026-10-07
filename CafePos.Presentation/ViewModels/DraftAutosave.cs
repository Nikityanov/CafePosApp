using CafePos.Core.Models;
using CafePos.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// The draft: the notice the header shows, the debounced write of the cart, and the restore that
/// puts a saved cart back when the page is opened on an empty one.
/// </summary>
/// <remarks>
/// <para>
/// The fifth Collaborator. THREE dependencies, and the plan's second one is deliberately absent:
/// it predicted <see cref="TimeProvider"/> because <see cref="TimeProvider"/> is what this phase used
/// everywhere else. The debounce here is a FIXED TIMEOUT, not a clock reading — nothing here decides
/// what time it is. Adding the clock would have been a dependency in name only. The honest way to make
/// the delay testable later is <c>Task.Delay(delay, timeProvider, token)</c>, and that is a change of
/// mechanism, not something to smuggle in with a move.
/// </para>
/// <para>
/// <b>IT OWNS <see cref="DraftNotice"/> OUTRIGHT.</b> The word «Несохранённый» was dropped on the
/// owner's instruction, and the reason it reads as a property of the cart rather than a report of an
/// action is that it is a HEADER CONTINUATION — it sits where «Корзина» otherwise would, and «Корзина»
/// disappears while this is shown. Keeping the notice here rather than in the shell is what stops the
/// four places that write it from having to be kept in step.
/// </para>
/// </remarks>
public sealed class DraftAutosave : ObservableObject
{
    /// <summary>How long the cart has to be still before it is written.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private readonly IDraftOrderService drafts;
    private readonly CartBuilder cart;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? pending;
    private string notice = string.Empty;

    public DraftAutosave(IDraftOrderService drafts, CartBuilder cart, ILogger logger)
    {
        this.drafts = drafts;
        this.cart = cart;
        this.logger = logger;
    }

    /// <summary>The header continuation shown while a saved cart is on the till.</summary>
    public string DraftNotice
    {
        get => notice;
        private set
        {
            if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasDraftNotice));
        }
    }

    /// <summary>Whether the header shows the notice instead of the word «Корзина».</summary>
    public bool HasDraftNotice => !string.IsNullOrWhiteSpace(DraftNotice);

    /// <summary>
    /// Drops the notice, for the moments the cart is no longer a restored draft — an order finished,
    /// a cart parked, a parked cart taken back up.
    /// </summary>
    /// <remarks>
    /// One call rather than an assignment per site. Three of the four sites were exactly this line, and
    /// a fourth fact added to the notice later would have silently survived them all.
    /// </remarks>
    public void ClearNotice() => DraftNotice = string.Empty;

    /// <summary>
    /// Puts a saved cart back, when there is nothing on the cart to lose.
    /// </summary>
    /// <remarks>
    /// The guard is <c>Cart.Count &gt; 0</c> and it is the whole contract: this runs on every return to
    /// the tab, and a cart the cashier is building must never be overwritten by a draft from earlier.
    /// </remarks>
    public async Task RestoreAsync()
    {
        if (cart.Cart.Count > 0) return;

        var snapshot = await drafts.LoadActiveCartAsync();
        if (snapshot.IsEmpty) return;

        // Through WithComponents so a restored bundle keeps its slots. A combo that arrives without
        // them prints as one line with nothing under it AND loses its merge signature, so it would
        // merge with an identical bundle and split from itself.
        cart.RestoreLines([.. snapshot.Lines.Select(line => CartItemViewModel.FromLine(line).WithComponents(line.Components))]);
        cart.Recalculate();
        // The header continuation, not the message strip — see DraftNotice. The word
        // «Восстановлен» was dropped on the owner's instruction: the header already says
        // «Корзина», so the note reads as a property of the cart rather than as a report
        // of an action nobody performed in this session.
        DraftNotice = snapshot.SavedAt is { } savedAt
            ? $"несохранённый чек от {savedAt.ToLocalTime():HH:mm}"
            : "несохранённый чек";
    }

    /// <summary>
    /// Debounced autosave. The lines are snapshotted on the calling (UI) thread, the write itself
    /// happens after a short delay so typing/stepping does not hit the database on every tap.
    /// </summary>
    public void Schedule()
    {
        // Cancel but do NOT dispose. The superseded AutoSaveAsync still holds this token and
        // may be inside drafts.SaveActiveCartAsync right now; disposing a CancellationTokenSource
        // whose token is in use is a race of its own. The superseded run disposes its own source
        // when it finishes.
        pending?.Cancel();
        var cancellation = new CancellationTokenSource();
        pending = cancellation;

        var lines = cart.ToCheckoutLines();
        _ = AutoSaveAsync(lines, cancellation);
    }

    private async Task AutoSaveAsync(IReadOnlyList<CheckoutLine> lines, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(Delay, cancellation.Token);

            // The gate is taken AFTER the debounce and released in finally, so an overlapping
            // snapshot waits for the in-flight write instead of racing it. The superseded
            // snapshot is already cancelled by then and its SaveChanges is skipped, so the
            // waiter writes the newer lines and the last write still wins.
            await gate.WaitAsync(cancellation.Token);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await drafts.SaveActiveCartAsync(lines, cancellation.Token);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change superseded this snapshot.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cart autosave failed");
        }
        finally
        {
            // Only the source this run owns, and only if it has not already been replaced.
            if (ReferenceEquals(pending, cancellation)) pending = null;
            cancellation.Dispose();
        }
    }
}
