namespace CafePos.Core.Services;

/// <summary>
/// Everything one order needs, and nothing about shifts or reports.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate port: read an order, move it, take its money. Most order screens want exactly this
/// and nothing else, and giving each of them three constructor parameters
/// (<see cref="IOrderQueries"/>, <see cref="IOrderCommands"/>, <see cref="IOrderPayments"/>) would say
/// the same thing more loudly.
/// </para>
/// <para>
/// What it deliberately EXCLUDES is the point of it. The orders board and the order details screen
/// used to hold the flat twenty-three-method <c>IOrderService</c>, which meant both could in principle
/// call <c>CloseShiftAsync</c> — reconciling a drawer from a screen that displays orders. Neither needs
/// a shift to do its job, so neither gets a shift.
/// </para>
/// <para>
/// The three verb-level ports stay public for a caller that needs exactly one — nothing above them
/// forces them together.
/// </para>
/// </remarks>
public interface IOrderOperations :
    IOrderQueries,
    IOrderCommands,
    IOrderPayments;
