using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>
/// What switching an order between counter service and takeaway costs the operator.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE ONLY NON-ROUTINE SWITCH ON THE CART SCREEN.</b> Moving to counter service deletes a phone
/// number the operator typed, and it deletes the row showing it in the same frame. That is not a
/// state anyone can read off a row afterwards, so it is the one place this screen earns a message —
/// and the message is worth spending only on the loss, never on the ordinary tap.
/// </para>
/// <para>
/// The decision is pure: (current, target, has phone) in, (what changes, what to say) out. The
/// ViewModel applies it and owns the notification. This is the third application of the same split as
/// <see cref="ProductAddFlow"/> and <see cref="BundlePlan"/>.
/// </para>
/// </remarks>
/// <param name="OrderType">The type the order becomes.</param>
/// <param name="Changed">
/// False when <c>target</c> was already the order's type, so the caller can return early. Carried
/// explicitly rather than inferred from the other three fields: a silent move to takeaway looks
/// exactly like a no-op on those fields alone, and a toggle pressed twice MUST be inert.
/// </param>
/// <param name="DropsPhone">
/// True when a typed phone number is about to be destroyed. This is the signal to speak, and it is
/// deliberately NOT the same as <see cref="ClearsPhone"/>: the phone property is cleared on every move
/// to counter service, but only a move that loses a number is worth a sentence.
/// </param>
/// <param name="ClearsPhone">Whether <c>CustomerPhone</c> must be set to null.</param>
/// <param name="Announce">
/// The sentence to show, or <c>null</c> for silence. Silence means <b>do not touch the message
/// property at all</b> — assigning an empty string would still retire a pending undo, because that is
/// what <c>Message</c>'s setter does, and «Отменить» armed by a removal has to survive a tap on the
/// fulfilment button.
/// </param>
public sealed record FulfilmentSwitch(
    OrderType OrderType,
    bool Changed,
    bool DropsPhone,
    bool ClearsPhone,
    string? Announce)
{
    /// <summary>Wording that names the loss rather than the policy, so the operator knows what happened.</summary>
    public const string PhoneDroppedMessage = "Телефон удалён: заказ в зале, номер не сохраняем.";

    /// <summary>
    /// Decides the consequences of moving an order to <paramref name="target"/>.
    /// </summary>
    /// <param name="current">What the order is now.</param>
    /// <param name="target">What it is becoming. Equal to <paramref name="current"/> means nothing changes.</param>
    /// <param name="hasPhone">Whether a phone number has actually been typed — blank is not a number.</param>
    public static FulfilmentSwitch Decide(OrderType current, OrderType target, bool hasPhone)
    {
        if (current == target) return new FulfilmentSwitch(current, Changed: false, false, false, Announce: null);

        var clears = target == OrderType.CounterService;

        // Read from what was there BEFORE the drop. After the phone is cleared there is nothing left to
        // ask about, which is the whole reason this has to be decided up front.
        var drops = clears && hasPhone;

        return new FulfilmentSwitch(target, Changed: true, drops, clears, drops ? PhoneDroppedMessage : null);
    }
}