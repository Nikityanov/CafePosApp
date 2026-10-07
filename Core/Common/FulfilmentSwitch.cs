using CafePos.Core.Models;

namespace CafePos.Core.Common;

/// <summary>What switching an order between counter service and takeaway costs the operator.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public sealed record FulfilmentSwitch(
    OrderType OrderType,
    bool Changed,
    bool DropsPhone,
    bool ClearsPhone,
    string? Announce)
{
    /// <summary>Wording that names the loss rather than the policy, so the operator knows what happened.</summary>
    public const string PhoneDroppedMessage = "Телефон удалён: заказ в зале, номер не сохраняем.";

    /// <summary>Decides the consequences of moving an order to <paramref name="target"/>.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

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
