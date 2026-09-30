namespace CafePos.Core.Common;

/// <summary>
/// All monetary values are persisted as integer kopecks, because SQLite stores decimals as TEXT:
/// TEXT columns break SUM/ORDER BY/comparisons in SQL and are not supported by decimal aggregates.
/// The domain exposes decimals through <see cref="Money"/> so that callers keep using rubles.
/// </summary>
public static class Money
{
    public const int KopecksPerRuble = 100;

    /// <summary>Rounds a ruble amount to 2 decimals with commercial (away from zero) rounding.</summary>
    public static decimal Round(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Converts rubles to kopecks using commercial rounding.</summary>
    public static long ToKopecks(decimal amount) =>
        (long)decimal.Round(amount * KopecksPerRuble, 0, MidpointRounding.AwayFromZero);

    /// <summary>Converts kopecks back to rubles.</summary>
    public static decimal FromKopecks(long kopecks) => kopecks / (decimal)KopecksPerRuble;

    /// <summary>Applies a percentage change (e.g. 10 => +10%, -15 => -15%) and rounds the result.</summary>
    public static decimal ApplyPercent(decimal amount, decimal percent) =>
        Round(amount * (1 + percent / 100m));
}
