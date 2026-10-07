using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>Reading a shift as numbers: aggregates, the product breakdown, and the overridden-price lines.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public interface IOrderReporting
{
    /// <summary>All shift aggregates.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<ShiftStats> GetShiftStatsAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>The shift's product breakdown, one row per distinct product × modifier × variant, with the section each dish currently belongs to.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<List<ProductAnalyticsRowData>> GetProductAnalyticsAsync(Guid shiftId, CancellationToken cancellationToken = default);

    /// <summary>The shift's lines whose charged price differs from the price they were allowed to be sold at: the "Скидки" section of the shift report.</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    Task<List<DiscountedLine>> GetDiscountedLinesAsync(Guid shiftId, CancellationToken cancellationToken = default);
}
