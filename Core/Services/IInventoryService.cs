using CafePos.Core.Models;

namespace CafePos.Core.Services;

public interface IInventoryService
{
    /// <summary>Ingredients at or below their minimum level. Compared in memory, never in SQL.</summary>
    Task<List<Ingredient>> GetLowStockAsync(CancellationToken cancellationToken = default);

    Task<List<StockMovement>> GetMovementsAsync(Guid ingredientId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Adds stock (delivery / manual correction) and records the movement.</summary>
    Task RestockAsync(Guid ingredientId, decimal quantity, string? comment = null, CancellationToken cancellationToken = default);

    /// <summary>Preview of the shortages for the given order lines, used by the cart before checkout.</summary>
    Task<List<StockShortage>> PreviewShortagesAsync(IReadOnlyList<(Guid ProductId, int Quantity)> lines, CancellationToken cancellationToken = default);
}
