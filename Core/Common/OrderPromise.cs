namespace CafePos.Core.Common;

/// <summary>When the customer was promised the order, and what the till says about it.</summary>
/// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

public static class OrderPromise
{
    /// <summary><param name="timeOfDay">The clock time the operator chose.</param> <param name="now"> What "now" is, from the ViewModel's `TimeProvider`.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static DateTimeOffset At(TimeSpan timeOfDay, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(now.DateTime.Date + timeOfDay, DateTimeKind.Unspecified);

        /// <summary>The offset for THAT wall-clock time, not for now.</summary>
        /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>True when a promise exists and has already passed. No promise is not late — the ordinary order is not an overdue one.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static bool IsLate(DateTimeOffset? requestedAt, DateTimeOffset now) =>
        requestedAt is { } at && at <= now;

    /// <summary>What the "когда" control says once a clock time has been chosen.</summary>
    /// <remarks>Почему так — `docs/decisions/shared.md`</remarks>

    public static string Describe(DateTimeOffset requestedAt, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var clockText = ClockTime.Format(requestedAt, zone);

        return IsLate(requestedAt, now)
            ? $"Заказ к {clockText} — время уже прошло, заказ просрочен."
            : $"Заказ к {clockText}.";
    }

    /// <summary>What the sheet says when the operator chooses "as soon as possible".</summary>
    public const string AsSoonAsPossibleMessage = "Как можно скорее — время по умолчанию.";
}
