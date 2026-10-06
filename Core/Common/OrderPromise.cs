namespace CafePos.Core.Common;

/// <summary>
/// When the customer was promised the order, and what the till says about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS WAS THREE PRIVATE METHODS ON A VIEWMODEL AND WAS NOT TESTABLE.</b> Placing a clock time on a
/// date, deciding whether the promise has already passed, and wording the message are all pure
/// functions of (what was picked, what time it is now) — and all three lived inside
/// <c>MenuViewModel</c>, where a test could not reach a single one of them. The time placement in
/// particular carries the rule that a time which has ALREADY PASSED stays on today, and the daylight
/// -saving reasoning, and neither could be asserted without an emulator.
/// </para>
/// <para>
/// The ViewModel keeps the sheet and the assignment; this type answers the questions. Same split as
/// <see cref="ProductAddFlow"/> and <see cref="BundlePlan"/>: the decision is pure, the awaiting is
/// not.
/// </para>
/// </remarks>
public static class OrderPromise
{
    /// <param name="timeOfDay">The clock time the operator chose.</param>
    /// <param name="now">
    /// What "now" is, from the ViewModel's <see cref="TimeProvider"/>. Passed in rather than read from
    /// the clock here so the rule can be asserted at a fixed instant, and so the ViewModel stays the
    /// only thing holding a clock.
    /// </param>
    /// <param name="zone">
    /// The zone the promise is expressed in. Defaults to <see cref="TimeZoneInfo.Local"/>, which is what
    /// <c>new DateTimeOffset(unspecified)</c> used — and using it implicitly is why this rule could
    /// not be tested: the offset was resolved against the MACHINE's zone, so a ViewModel driven by a
    /// TimeProvider pinned to some other zone would place the promise in one zone and print it in
    /// another. Taking the zone from the same provider that supplied <paramref name="now"/> keeps the
    /// two in step and lets the daylight-saving rule be asserted at all.
    /// </param>
    public static DateTimeOffset At(TimeSpan timeOfDay, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(now.DateTime.Date + timeOfDay, DateTimeKind.Unspecified);

        // The offset for THAT wall-clock time, not for now. Across a daylight-saving change the two
        // differ by an hour, and an order promised for a time whose offset was taken from the wrong
        // side is an hour out for as long as it sits on the board.
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>
    /// True when a promise exists and has already passed. No promise is not late — the ordinary order
    /// is not an overdue one.
    /// </summary>
    /// <remarks>
    /// A computed reading of the wall clock rather than a stored flag, so it cannot disagree with the
    /// order. It is therefore only as fresh as the last time something announced it, which is why the
    /// ViewModel re-raises it on every return to the tab.
    /// </remarks>
    public static bool IsLate(DateTimeOffset? requestedAt, DateTimeOffset now) =>
        requestedAt is { } at && at <= now;

    /// <summary>
    /// What the "когда" control says once a clock time has been chosen.
    /// </summary>
    /// <remarks>
    /// <b>NON-NULLABLE, DELIBERATELY.</b> There is a case where nothing is promised — as soon as
    /// possible — and it has its own sentence in <see cref="AsSoonAsPossibleMessage"/>. Folding that
    /// case in here would mean returning <c>string?</c> and making every caller cope with a null that
    /// its own control flow has already excluded; this type says what it means and the caller assigns
    /// it directly.
    /// <para>
    /// Overdue is stated out loud, because it is not obvious from the figure. «Заказ к 14:20» at 15:05
    /// is a different order from «Заказ к 14:20» at 13:00, and the sentence has to say which one this
    /// is — otherwise the operator reads their own confirmation as a mistake.
    /// </para>
    /// </remarks>
    /// <param name="requestedAt">The promise now on the order. Not null: this describes a promise.</param>
    /// <param name="now">What "now" is, from the ViewModel's clock.</param>
    /// <param name="zone">
    /// The zone to read the clock time back in. Defaults to <see cref="TimeZoneInfo.Local"/>, matching
    /// <see cref="At"/>. It matters that the two agree: a promise placed with one zone's offset and
    /// printed in another's is an hour out on the message the operator reads back to the customer.
    /// </param>
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