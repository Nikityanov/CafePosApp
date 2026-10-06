using System.ComponentModel.DataAnnotations.Schema;
using CafePos.Core.Common;

namespace CafePos.Core.Models;

/// <summary>
/// Parked ("held") cart. Unsent carts are persisted so a crash or an accidental
/// page reload never destroys a half-finished order.
/// </summary>
/// <remarks>
/// The order-level facts the till collected are mirrored here — <see cref="OrderType"/>,
/// <see cref="CustomerPhone"/>, <see cref="RequestedAt"/> — so that parking and forgetting does not
/// throw away what the operator entered. A parked cart is not an order: it is in no queue, it is
/// promised to nobody and it is reconciled against no drawer, which is why the promise arithmetic of
/// <see cref="Order.PromisedAt"/> is deliberately NOT duplicated here. Nothing would read it, and a
/// second copy of a derived figure is a second thing to go stale.
/// </remarks>
public class DraftOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? ShiftId { get; set; }

    /// <summary>True for the single autosaved cart of the current operator.</summary>
    public bool IsActiveCart { get; set; }

    /// <summary>How the parked order is fulfilled. Decides whether <see cref="CustomerPhone"/> is kept.</summary>
    public OrderType OrderType { get; set; }

    /// <summary>
    /// Contact for a takeaway order, in E.164. Held here only because the operator entered it before
    /// the order existed; the checkout still decides whether it may be written to the order itself
    /// (see <see cref="Order.CustomerPhone"/>).
    /// </summary>
    public string? CustomerPhone { get; set; }

    /// <summary>What the customer asked for, or <c>null</c> for as soon as possible.</summary>
    public DateTimeOffset? RequestedAt { get; set; }

    public List<DraftOrderItem> Items { get; set; } = new();

    [NotMapped]
    public int TotalQuantity => Items.Sum(item => item.Quantity);

    [NotMapped]
    public decimal Total => Money.FromKopecks(Items.Sum(item => item.LineTotalKopecks));
}

/// <summary>A single line of a parked cart.</summary>
public class DraftOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DraftOrderId { get; set; }
    public DraftOrder DraftOrder { get; set; } = null!;
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public long PriceKopecks { get; set; }

    [NotMapped]
    public decimal Price
    {
        get => Money.FromKopecks(PriceKopecks);
        set => PriceKopecks = Money.ToKopecks(value);
    }

    public string? SelectedModifierName { get; set; }
    public string? SelectedVariantName { get; set; }
    public int Quantity { get; set; }

    /// <summary>
    /// The composition of a bundle line, carried through the parking lot exactly as
    /// <see cref="OrderItem.Components"/> carries it through checkout. Rewritten with the line on
    /// every autosave, which is why it is a separate table and not a column.
    /// </summary>
    public List<DraftOrderItemComponent> Components { get; set; } = new();

    [NotMapped]
    public long LineTotalKopecks => PriceKopecks * Quantity;
}
