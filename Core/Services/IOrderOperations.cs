namespace CafePos.Core.Services;

/// <summary>Everything one order needs, and nothing about shifts or reports.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IOrderOperations :
    IOrderQueries,
    IOrderCommands,
    IOrderPayments;
