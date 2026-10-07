using CafePos.Core.Common;
using CafePos.Core.Services;

namespace CafePos.Presentation.ViewModels;
/// classes because they are two responsibilities - they shared a file, not a reason.
/// </remarks>
public partial class MenuViewModel
{
    /// <summary>
    /// The order-level facts this cart carries, gathered above the cart and passed as one value.
    /// </summary>
    /// <remarks>
    /// One parameter rather than three, because they are facts about the ORDER and not about any line:
    /// a customer either takes the whole thing away or eats all of it on the premises. Carrying them
    /// per line would allow one order to be half takeaway.
    /// <para>
    /// The phone goes in as typed and is normalised by the service, which also DROPS it for counter
    /// service - so passing one here for a counter order would be pointless as well as unlawful. The
    /// control makes that unreachable, and the service makes it enforced.
    /// </para>
    /// </remarks>
    private OrderDetailsIntent BuildOrderDetails() => new(OrderType, CustomerPhone, RequestedAt);

    /// <summary>
    /// «Оплатить {total}»: hand the sale to the coordinator, and clear the till if it booked one.
    /// </summary>
    private async Task PayAndCreateAsync()
    {
        var result = await checkoutCoordinator.PayAndCreateAsync(BuildOrderDetails());

        switch (result.Outcome)
        {
            case SaleOutcome.Booked:
                await ClearForNextOrderAsync();
                Message = $"Заказ #{result.OrderNumber} создан на {TextFormat.Money(result.TotalPrice)}, {result.ReceiptText}.";
                break;

            // The only outcome with something for the shell to do. Navigation is not the coordinator's
            // business, which is why it is an answer rather than a call it made itself.
            case SaleOutcome.OfferShift:
                await navigation.GoToOpenShiftAsync();
                break;
        }
    }

    /// <summary>
    /// Names the cart and sets it aside.
    /// </summary>
    private async Task ParkOrderAsync()
    {
        if (await parkedCarts.ParkAsync() is null) return;
        await ClearForNextOrderAsync();
    }

    /// <summary>
    /// Picks a parked receipt and puts it back on the cart.
    /// </summary>
    /// <remarks>
    /// Clears only the draft notice, NOT the order-level facts: a parked receipt was saved without a
    /// phone or a promised time - see the KNOWN GAP note in the plan - so there is nothing here that
    /// belongs to the order being started. The screen is empty either way.
    /// </remarks>
    private async Task OpenParkedAsync()
    {
        if (await parkedCarts.OpenAsync()) autosave.ClearNotice();
    }

    /// <summary>
    /// Empties the till for the next sale: the cart, the order-level facts, the header note and the
    /// saved draft.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method for three callers that all had to agree, because they each used to do part of it.
    /// </para>
    /// <para>
    /// <b>Clearing THE ORDER-LEVEL FACTS IS A PRIVACY REQUIREMENT, NOT TIDINESS.</b> A phone left on an
    /// emptied screen would be carried into the NEXT order and written to it by
    /// <see cref="BuildOrderDetails"/> with nobody having entered it for that sale. The fulfilment type
    /// goes back to counter service for the same reason: the default is the sale that stores the least
    /// personal data, so a phone can never reach an order by inertia.
    /// <para>
    /// The header note goes too, for a different reason: left in place it would head an empty
    /// «Корзина» with a note about an order that has now been paid for.
    /// </para>
    /// </remarks>
    private async Task ClearForNextOrderAsync()
    {
        cart.Cart.Clear();
        cart.Recalculate();
        fulfilment.Reset();
        autosave.ClearNotice();
        await drafts.ClearActiveCartAsync();
    }
}
