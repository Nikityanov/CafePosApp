using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

/// <summary>
/// The promise made to a customer: when it is, whether it has already passed, and what the till says.
/// </summary>
/// <remarks>
/// <para>
/// <b>THREE PRIVATE METHODS ON A VIEWMODEL, NONE OF THEM TESTABLE.</b> Placing a clock time on a date,
/// deciding whether the promise has passed, and wording the message were all inside
/// <c>MenuViewModel</c>, so a test could not reach one of them. The placement in particular carries the
/// rule that a time which has ALREADY PASSED stays on today and the daylight-saving reasoning, and
/// neither could be asserted without an emulator — which is exactly where you do not want to be
/// asserting them.
/// </para>
/// <para>
/// The cases below are distinct ways of being wrong, not examples of one rule. Midnight, the
/// daylight-saving hour and the already-passed time are the three that a naive implementation gets
/// wrong in three different ways.
/// </para>
/// </remarks>
public class OrderPromiseTests
{
    /// <summary>
    /// A fixed zone, deliberately at an offset no developer machine is plausibly set to.
    /// </summary>
    /// <remarks>
    /// <b>WHY +05:45 AND NOT UTC.</b> The property under test is that the promise is placed in the zone
    /// it was TOLD to use rather than in the machine's. An assertion against <c>TimeZoneInfo.Local</c>
    /// proves nothing on a machine whose local zone happens to match the test's. Half-hour offsets are
    /// near enough to unique that this cannot pass by coincidence — which is the only thing that makes
    /// a "did it use my zone?" test worth writing.
    /// </remarks>
    private static readonly TimeZoneInfo Kathmandu = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC+05:45",
        new TimeSpan(5, 45, 0),
        "Test +05:45",
        "Test +05:45");

    /// <summary>A second zone on the other side of the planet, for the same reason.</summary>
    private static readonly TimeZoneInfo Newfoundland = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC-03:30",
        new TimeSpan(-3, -30, 0),
        "Test -03:30",
        "Test -03:30");

    /// <summary>
    /// A fixed instant, read as a wall-clock time IN THE GIVEN ZONE.
    /// </summary>
    /// <remarks>
    /// <b>THE ZONE IS A PARAMETER, NOT A CONSTANT, AND THAT IS THE POINT.</b> Building <c>now</c> at a
    /// hardcoded +3 while the promise is placed at +05:45 compares two different frames: 13:00 at +3 is
    /// 10:00 UTC, which is BEFORE 14:20 at +05:45, so a promise two hours into the future came back
    /// overdue. Every comparison in this file needs both sides in one zone.
    /// </remarks>
    private static DateTimeOffset At(int hour, int minute, TimeZoneInfo? zone = null)
    {
        var target = zone ?? Kathmandu;
        var unspecified = DateTime.SpecifyKind(new DateTime(2026, 10, 6, hour, minute, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, target.GetUtcOffset(unspecified));
    }

    [Fact]
    public void A_chosen_time_lands_on_the_date_the_operator_looked_at()
    {
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), At(17, 0), Kathmandu);

        Assert.Equal(new TimeSpan(14, 20, 0), TimeZoneInfo.ConvertTime(promise, Kathmandu).TimeOfDay);
        Assert.Equal(2026, TimeZoneInfo.ConvertTime(promise, Kathmandu).Year);
        Assert.Equal(6, TimeZoneInfo.ConvertTime(promise, Kathmandu).Day);
    }

    [Fact]
    public void A_time_that_has_already_passed_STAYS_ON_TODAY_rather_than_rolling_forward()
    {
        // THE RULE. It used to roll to tomorrow, which silently rewrote what the operator said: they
        // pick 14:20 at 15:00 and get an order promised for tomorrow. It also made an overdue order
        // inexpressible, and a customer who said «в 14:20» and is still waiting at 15:00 is the
        // ordinary reason this control exists.
        var now = At(15, 0);
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), now, Kathmandu);

        var local = TimeZoneInfo.ConvertTime(promise, Kathmandu);

        Assert.Equal(6, local.Day);
        Assert.Equal(14, local.Hour);
        Assert.True(promise < now, "and the result must be genuinely in the past, which is what makes it overdue");
        Assert.True(OrderPromise.IsLate(promise, now));
    }

    [Fact]
    public void An_ordinary_future_time_is_not_late()
    {
        var now = At(13, 0);
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), now, Kathmandu);

        Assert.False(OrderPromise.IsLate(promise, now));
    }

    [Fact]
    public void No_promise_at_all_is_not_late()
    {
        // The ordinary order is not an overdue one. Reporting "late" for a null would put an overdue
        // badge on every order that was never given a time.
        Assert.False(OrderPromise.IsLate(null, At(17, 0)));
    }

    [Fact]
    public void A_promise_exactly_now_is_late()
    {
        // The board treats a promise that has arrived as not-scheduled, so the badge and the board must
        // agree, and that means `<=` rather than `<`.
        var now = At(14, 20);

        Assert.True(OrderPromise.IsLate(now, now));
    }

    [Fact]
    public void Midnight_stays_midnight_on_the_same_day_and_does_not_become_noon()
    {
        // A TimeSpan of 24 hours would be an hour out, and `now.Date + timeOfDay` crossing midnight is
        // the case that silently moves the promise to the next day.
        var now = At(23, 50);
        var promise = OrderPromise.At(TimeSpan.Zero, now, Kathmandu);

        var local = TimeZoneInfo.ConvertTime(promise, Kathmandu);

        Assert.Equal(0, local.Hour);
        Assert.Equal(0, local.Minute);
        Assert.Equal(6, local.Day);
    }

    [Fact]
    public void The_offset_comes_from_the_zone_it_was_given_and_not_from_the_machine()
    {
        // The reason At takes a zone at all. `now` is expressed at +3, the zone says +05:45, and the
        // two disagree by nearly three hours. An implementation that ignored the zone and used
        // `new DateTimeOffset(unspecified)` - which is exactly what this one used to do - would place
        // the promise on the developer's or the till's own offset, and the rule could not be
        // asserted here at all.
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), At(17, 0), Kathmandu);

        Assert.Equal(new TimeSpan(5, 45, 0), promise.Offset);
        Assert.Equal("14:20", ClockTime.Format(promise, Kathmandu));
    }

    [Fact]
    public void A_second_zone_gets_its_own_offset_and_the_same_wall_clock_time()
    {
        // Two zones, so the test cannot be satisfied by handing the clock time back unchanged and
        // hoping. Both keep 14:20 as 14:20 - a promise is a wall-clock promise - while differing by
        // nine hours and fifteen minutes of offset.
        var inKathmandu = OrderPromise.At(new TimeSpan(14, 20, 0), At(17, 0), Kathmandu);
        var inNewfoundland = OrderPromise.At(new TimeSpan(14, 20, 0), At(17, 0, Newfoundland), Newfoundland);

        Assert.Equal(new TimeSpan(5, 45, 0), inKathmandu.Offset);
        Assert.Equal(new TimeSpan(-3, -30, 0), inNewfoundland.Offset);

        // Each reads back as the time that was chosen, in its own zone - which is what the message the
        // cashier reads back to the customer has to say.
        Assert.Equal("14:20", ClockTime.Format(inKathmandu, Kathmandu));
        Assert.Equal("14:20", ClockTime.Format(inNewfoundland, Newfoundland));
    }

    [Fact]
    public void Reading_the_same_promise_back_in_another_zone_gives_a_different_clock_time()
    {
        // Not a rule but a measurement, and it is WHY the zone is threaded through Describe as well as
        // At. One instant, two zones, two different clock times - and the cashier is shown the second
        // one.
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), At(17, 0), Kathmandu);

        Assert.NotEqual(ClockTime.Format(promise, Kathmandu), ClockTime.Format(promise, Newfoundland));
    }

    [Fact]
    public void The_message_says_so_when_the_promise_has_already_passed()
    {
        // Stated out loud, because it is not obvious from the figure: «Заказ к 14:20» at 15:05 is a
        // different order from the same words at 13:00, and without the sentence the operator reads
        // their own confirmation as a mistake.
        var now = At(15, 5);
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), now, Kathmandu);

        var message = OrderPromise.Describe(promise, now, Kathmandu);

        Assert.Equal("Заказ к 14:20 — время уже прошло, заказ просрочен.", message);
    }

    [Fact]
    public void The_message_is_plain_when_the_promise_is_still_to_keep()
    {
        var now = At(13, 0);
        var promise = OrderPromise.At(new TimeSpan(14, 20, 0), now, Kathmandu);

        Assert.Equal("Заказ к 14:20.", OrderPromise.Describe(promise, now, Kathmandu));
    }
}

/// <summary>
/// What switching an order between counter service and takeaway costs the operator.
/// </summary>
/// <remarks>
/// <para>
/// The one switch on the cart screen that destroys something the operator typed. Its message is worth
/// spending on the loss and only on the loss, because the strip earns its height by carrying things
/// the screen does not already show.
/// </para>
/// </remarks>
public class FulfilmentSwitchTests
{
    [Fact]
    public void Switching_to_takeaway_keeps_the_phone_and_says_nothing()
    {
        // The ordinary tap. A sentence here would be noise, and noise teaches the operator to read
        // past the strip.
        var change = FulfilmentSwitch.Decide(OrderType.CounterService, OrderType.Takeaway, hasPhone: true);

        Assert.True(change.Changed);
        Assert.Equal(OrderType.Takeaway, change.OrderType);
        Assert.False(change.DropsPhone);
        Assert.False(change.ClearsPhone);
        Assert.Null(change.Announce);
    }

    [Fact]
    public void Switching_back_to_the_counter_drops_a_typed_phone_and_says_what_was_lost()
    {
        var change = FulfilmentSwitch.Decide(OrderType.Takeaway, OrderType.CounterService, hasPhone: true);

        Assert.True(change.Changed);
        Assert.Equal(OrderType.CounterService, change.OrderType);
        Assert.True(change.DropsPhone);
        Assert.True(change.ClearsPhone);
        Assert.Equal(FulfilmentSwitch.PhoneDroppedMessage, change.Announce);
    }

    [Fact]
    public void The_wording_names_the_loss_rather_than_the_policy()
    {
        // «Телефон удалён» tells the operator what happened to their typing. A sentence about the rule
        // («номер не сохраняется в зале») would describe the shop's policy instead.
        Assert.Contains("удалён", FulfilmentSwitch.PhoneDroppedMessage);
        Assert.DoesNotContain("запрещ", FulfilmentSwitch.PhoneDroppedMessage);
    }

    [Fact]
    public void Switching_with_no_phone_still_clears_the_property_but_stays_silent()
    {
        // DropsPhone and ClearsPhone are deliberately NOT the same flag. The property is cleared on
        // every move to the counter; only a move that LOSES a number is worth a sentence.
        var change = FulfilmentSwitch.Decide(OrderType.Takeaway, OrderType.CounterService, hasPhone: false);

        Assert.True(change.ClearsPhone);
        Assert.False(change.DropsPhone);
        Assert.Null(change.Announce);
    }

    [Fact]
    public void Switching_to_the_type_already_set_changes_nothing_at_all()
    {
        // What makes the toggle safe to press twice. Without an explicit Changed flag this is
        // indistinguishable from a silent move to takeaway, and a second press would wipe the phone.
        var change = FulfilmentSwitch.Decide(OrderType.Takeaway, OrderType.Takeaway, hasPhone: true);

        Assert.False(change.Changed);
        Assert.False(change.DropsPhone);
        Assert.False(change.ClearsPhone);
        Assert.Null(change.Announce);
        Assert.Equal(OrderType.Takeaway, change.OrderType);
    }

    [Fact]
    public void A_no_op_switch_never_drops_a_phone_even_though_the_counter_service_clears_it()
    {
        var change = FulfilmentSwitch.Decide(OrderType.CounterService, OrderType.CounterService, hasPhone: true);

        Assert.False(change.Changed);
        Assert.False(change.ClearsPhone);
        Assert.Null(change.Announce);
    }
}
