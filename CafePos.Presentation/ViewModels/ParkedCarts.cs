using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Core.Errors;
using CafePos.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace CafePos.Presentation.ViewModels;

/// <summary>Setting a cart aside under a name, and picking one back up.</summary>
/// <remarks>
/// <para>
/// Parking is not selling. No money moves, no stock is pre-flighted, no order is created - and the
/// rows in the underlying tables differ accordingly. It sat in the same file as the checkout and the
/// plan therefore named one class for both; they are two responsibilities and two classes.
/// </para>
/// <para>
/// It takes the draft service, the picker and the clock - and that clock is a real read here, unlike
/// the debounce in <c>DraftAutosave</c>: «Чек 14:30» is the suggested NAME of the parked receipt, and
/// a name is a fact about when the cashier parked it.
/// </para>
/// </remarks>
public sealed class ParkedCarts
{
    private readonly IDraftOrderService drafts;
    private readonly IDraftPicker picker;
    private readonly IDialogService dialogs;
    private readonly TimeProvider timeProvider;
    private readonly CartBuilder cart;
    private readonly IHapticService haptics;
    private readonly ILogger logger;
    private readonly Action<string> announce;
    private readonly Action<string> sayError;

    public ParkedCarts(
        IDraftOrderService drafts,
        IDraftPicker picker,
        IDialogService dialogs,
        TimeProvider timeProvider,
        CartBuilder cart,
        IHapticService haptics,
        ILogger logger,
        Action<string> announce,
        Action<string> sayError)
    {
        this.drafts = drafts;
        this.picker = picker;
        this.dialogs = dialogs;
        this.timeProvider = timeProvider;
        this.cart = cart;
        this.haptics = haptics;
        this.logger = logger;
        this.announce = announce;
        this.sayError = sayError;
    }

    /// <summary>
    /// Names the cart and sets it aside, returning the parked receipt's name.
    /// </summary>
    /// <returns>The parked receipt's name, or <c>null</c> if nothing was parked.</returns>
    public async Task<string?> ParkAsync()
    {
        if (cart.Cart.Count == 0)
        {
            announce("Чек пуст.");
            return null;
        }

        try
        {
            var suggestedName = $"Чек {timeProvider.GetLocalNow():HH:mm}";
            var name = await dialogs.PromptAsync("Отложить чек", "Название отложенного чека", suggestedName);
            if (name is null) return null;

            var parked = await drafts.ParkAsync(name, cart.ToCheckoutLines());
            cart.Cart.Clear();
            cart.Recalculate();
            await drafts.ClearActiveCartAsync();
            announce($"Чек отложен: {parked.Name}.");
            return parked.Name;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to park the cart");
            sayError(UserMessages.Describe(exception, "Не удалось отложить чек"));
            return null;
        }
    }

    /// <summary>
    /// Picks a parked receipt and puts it back on the cart, replacing whatever was there.
    /// </summary>
    /// <returns>True when a parked receipt was loaded; false when nothing was or nothing was chosen.</returns>
    public async Task<bool> OpenAsync()
    {
        try
        {
            var parked = await drafts.GetParkedAsync();
            if (parked.Count == 0)
            {
                announce("Отложенных чеков нет.");
                return false;
            }

            if (cart.Cart.Count > 0 && !await dialogs.ConfirmAsync("Отложенные чеки", "Текущий чек будет очищен и заменён отложенным. Продолжить?"))
                return false;

            var draftId = await picker.PickAsync(parked);
            if (draftId is null) return false;

            var snapshot = await drafts.TakeAsync(draftId.Value);
            cart.Cart.Clear();
            // WithComponents, so a parked bundle comes back with its slots — see DraftAutosave.
            cart.RestoreLines([.. snapshot.Lines.Select(line => CartItemViewModel.FromLine(line).WithComponents(line.Components))]);
            cart.Recalculate();
            announce("Отложенный чек загружен.");
            haptics.Click();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to open a parked cart");
            sayError(UserMessages.Describe(exception, "Не удалось открыть отложенный чек"));
            return false;
        }
    }
}
