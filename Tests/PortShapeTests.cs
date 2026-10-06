using System.Reflection;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>
/// The shape of the five order ports: a screen may not hold authority it has no use for.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS AN ARCHITECTURE TEST, NOT A BEHAVIOUR TEST.</b> It asserts nothing about behaviour — no
/// order is created, no cash moves — and it would pass on an empty database. What it pins is a
/// structural fact that is otherwise invisible and trivially lost: that the flat twenty-three-method
/// <c>IOrderService</c> is not quietly reassembled one method at a time.
/// </para>
/// <para>
/// The concrete damage it prevents is known, not hypothetical.
/// <c>ShiftAnalyticsViewModel</c> held the flat interface and could call <c>CloseShiftAsync</c>: a
/// reporting screen with the authority to reconcile a drawer it only ever displays.
/// <c>BackupService</c> could call <c>RefundAsync</c>. Nothing stopped either — the interface offered,
/// the compiler agreed, and the mistake would only have surfaced as a wrong drawer at the count.
/// </para>
/// <para>
/// Assertions are on the METHOD SETS, because that is what was wrong. A comment promising restraint
/// is not a constraint; a missing method is.
/// </para>
/// </remarks>
public class PortShapeTests
{
    /// <summary>Only what this interface declares itself — used to prove the composite adds nothing.</summary>
    private static string[] DeclaredBy<TInterface>() =>
        typeof(TInterface).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Everything the interface exposes, inherited members included.
    /// <para>
    /// The two questions are separate and confusing them is the whole bug this file was written after:
    /// <c>DeclaredOnly</c> on the composite correctly returns nothing, because the composite declares
    /// no methods — so asking it for its "surface" that way returns an empty list and every comparison
    /// against it passes vacuously or fails for the wrong reason.
    /// </para>
    /// </summary>
    private static string[] MethodsOf<TInterface>() =>
        Surface(typeof(TInterface))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Every method an interface exposes, walking base interfaces by hand.
    /// </summary>
    /// <remarks>
    /// <c>Type.GetMethods()</c> on an interface returns only what that interface declares — members
    /// it INHERITS from another interface are not included, unlike a class. So for the composite,
    /// which declares nothing itself, the reflection call answers zero and every comparison against it
    /// is meaningless. Two wrong assumptions stacked here, both of which looked like a passing test
    /// until an assertion actually depended on the count.
    /// </remarks>
    private static IReadOnlyCollection<string> Surface(Type type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Type>();

        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            pending.Push(candidate);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var method in current.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                names.Add(method.Name);
            }

            foreach (var inherited in current.GetInterfaces())
            {
                pending.Push(inherited);
            }
        }

        return names;
    }

    [Fact]
    public void Reporting_cannot_close_the_shift()
    {
        // The defect that motivated the split, named rather than described.
        Assert.DoesNotContain(nameof(OrderService.CloseShiftAsync), DeclaredBy<IOrderReporting>());
        Assert.DoesNotContain(nameof(OrderService.OpenShiftAsync), DeclaredBy<IOrderReporting>());
    }

    [Fact]
    public void Reporting_cannot_move_money_or_orders()
    {
        Assert.Equal(
            new[] { "GetDiscountedLinesAsync", "GetProductAnalyticsAsync", "GetShiftStatsAsync" },
            DeclaredBy<IOrderReporting>());
    }

    [Fact]
    public void The_shift_ledger_cannot_refund_or_read_orders()
    {
        Assert.DoesNotContain(nameof(OrderService.RefundAsync), DeclaredBy<IShiftLedger>());
        Assert.DoesNotContain(nameof(OrderService.AddPaymentAsync), DeclaredBy<IShiftLedger>());
        Assert.DoesNotContain(nameof(OrderService.GetActiveOrdersAsync), DeclaredBy<IShiftLedger>());
    }

    [Fact]
    public void Order_screens_cannot_reconcile_the_drawer_or_read_reports()
    {
        Assert.DoesNotContain(nameof(OrderService.CloseShiftAsync), DeclaredBy<IOrderOperations>());
        Assert.DoesNotContain(nameof(OrderService.GetShiftStatsAsync), DeclaredBy<IOrderOperations>());
        Assert.DoesNotContain(nameof(OrderService.GetDiscountedLinesAsync), DeclaredBy<IOrderOperations>());
    }

    [Fact]
    public void Each_verb_port_is_a_strict_subset_of_the_composite()
    {
        // Guards against a port gaining something the composite no longer offers, which would mean
        // the two had drifted into describing different services under the same name.
        var composite = Surface(typeof(IOrderService));

        foreach (var port in new[] { typeof(IOrderQueries), typeof(IOrderCommands), typeof(IOrderPayments), typeof(IShiftLedger), typeof(IOrderReporting) })
        {
            var missing = Surface(port).Where(name => !composite.Contains(name)).ToArray();

            Assert.True(missing.Length == 0, $"{port.Name} declares methods IOrderService does not: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void The_composite_declares_no_methods_of_its_own()
    {
        // It is the union and nothing more. A method declared here belongs in one of the five.
        Assert.Empty(DeclaredBy<IOrderService>());
    }

    [Fact]
    public void The_composite_still_exposes_the_whole_surface_the_tests_exercise()
    {
        // The suite resolves IOrderService and crosses these lines on purpose, so the union has to
        // stay complete. This is the guard on the guard: if it ever shrinks, it fails here rather
        // than as a confusing missing-method error in some unrelated test.
        var expected = new[]
        {
            "AddContactDetailsAsync", "AddPaymentAsync", "AdvanceStatusAsync", "CancelOrderAsync",
            "CloseShiftAsync", "GetActiveOrdersAsync", "GetActiveShiftAsync", "GetCompletedOrdersAsync",
            "GetDiscountedLinesAsync", "GetLastCountedCashKopecksAsync", "GetLatestShiftAsync", "GetOrderAsync",
            "GetOrderPaymentsAsync", "GetProductAnalyticsAsync", "GetShiftOrderHistoryAsync", "GetShiftStatsAsync",
            "GetShiftsAsync", "GetStatusHistoryAsync", "MarkSeenAsync", "OpenShiftAsync", "RefundAsync",
            "UpdateOrderAsync",
        };

        var actual = MethodsOf<IOrderService>();

        Assert.Equal(expected.Length, actual.Length);
        Assert.Empty(actual.Except(expected));
    }

    [Fact]
    public void Every_port_is_implemented_by_the_single_order_service()
    {
        // One class, one DbContext, five ports. If this fails, someone added a port and is
        // expecting a second implementation — which would be the first sign of the register-per-port
        // pattern this split deliberately avoided.
        var service = typeof(OrderService);

        foreach (var port in new[] { typeof(IOrderQueries), typeof(IOrderCommands), typeof(IOrderPayments), typeof(IShiftLedger), typeof(IOrderReporting), typeof(IOrderOperations), typeof(IOrderService) })
        {
            Assert.True(port.IsAssignableFrom(service), $"OrderService does not implement {port.Name}");
        }
    }
}
