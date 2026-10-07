namespace CafePos.Core.Services;

/// <summary>A cash count the operator has already given, together with the drawer figure it was counted against.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public readonly record struct PendingCashCount(long CountedKopecks, long ExpectedAgainstKopecks);

/// <summary>What the end-of-shift count field should start with on a repeated attempt at closing a shift.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

public static class CashCountPrefill
{
    /// <summary>The figure in kopecks to pre-fill: the previous count when it was counted against the drawer as it stands now, and <paramref name="expectedKopecks"/>…</summary>
    /// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

    public static long Resolve(PendingCashCount? previous, long expectedKopecks) =>
        previous is { } entry && entry.ExpectedAgainstKopecks == expectedKopecks
            ? entry.CountedKopecks
            : expectedKopecks;
}
