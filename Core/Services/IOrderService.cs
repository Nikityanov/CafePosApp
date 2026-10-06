namespace CafePos.Core.Services;

/// <summary>
/// Every order concern in one type, for the callers that genuinely span them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a composite, not a god interface.</b> It declares no methods of its own — it is only the
/// union of five ports that each say one thing:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IOrderQueries"/> — listing and opening orders.</description></item>
/// <item><description><see cref="IOrderCommands"/> — status, lines, contact details.</description></item>
/// <item><description><see cref="IOrderPayments"/> — money in and out of one order.</description></item>
/// <item><description><see cref="IShiftLedger"/> — the shift and the drawer.</description></item>
/// <item><description><see cref="IOrderReporting"/> — the shift read as numbers.</description></item>
/// </list>
/// <para>
/// It was one flat interface of twenty-three methods before the split, which is how a reporting
/// screen came to hold the authority to close the till. <see cref="OrderService"/> implements all
/// five — it is one class and one DbContext, and nothing here duplicates behaviour — but a caller
/// should ask for the port it needs rather than this: <see cref="IOrderOperations"/> for order
/// screens, <see cref="IShiftLedger"/> for the till, <see cref="IOrderReporting"/> for the numbers.
/// </para>
/// <para>
/// Who legitimately needs the whole thing: the shift report, which reads aggregates AND closes the
/// shift AND voids an order that turns out to be wrong; and the test suite, which exercises
/// scenarios that cross the same lines on purpose.
/// </para>
/// </remarks>
public interface IOrderService :
    IOrderOperations,
    IShiftLedger,
    IOrderReporting;