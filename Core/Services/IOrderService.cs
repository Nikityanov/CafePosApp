namespace CafePos.Core.Services;

/// <summary>Every order concern in one type, for the callers that genuinely span them.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IOrderService :
    IOrderOperations,
    IShiftLedger,
    IOrderReporting;
