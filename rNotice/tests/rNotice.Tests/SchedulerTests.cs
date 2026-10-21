using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using rNotice.Core.Sources;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class SchedulerTests
{
    private static readonly IReadOnlyList<BusyBlock> Free = [];
    private readonly Scheduler _scheduler = new(new SchedulerOptions());

    private static IReadOnlyList<BusyBlock> InMeeting(ShowAs showAs = ShowAs.Busy, int endsInMinutes = 30) =>
        [new BusyBlock(Now.AddMinutes(-10), Now.AddMinutes(endsInMinutes), showAs)];

    [Fact]
    public void Due_items_fire_grouped_by_interrupt()
    {
        var result = _scheduler.Tick(
        [
            Item(Interrupt.Takeover, due: Now.AddSeconds(30)),
            Item(Interrupt.Alert),
            Item(Interrupt.Toast)
        ], Now, Free);

        Assert.Single(result.Takeovers);
        Assert.Single(result.Alerts);
        Assert.Single(result.Toasts);
    }

    [Fact]
    public void Not_yet_due_silent_closed_acked_and_already_fired_do_not_fire()
    {
        var result = _scheduler.Tick(
        [
            Item(fireAt: Now.AddMinutes(1)),
            Item(Interrupt.Silent),
            Item(stage: Stage.Done),
            Item(stage: Stage.Dismissed),
            Item() with { AckedAtUtc = Now.AddMinutes(-2) },
            Item() with { LastFiredUtc = Now.AddMinutes(-1) },
            Item() with { FireAtUtc = null }
        ], Now, Free);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Deferred);
    }

    [Fact]
    public void Planned_items_still_fire() =>
        Assert.Single(_scheduler.Tick([Item(stage: Stage.Planned)], Now, Free).Alerts);

    [Fact]
    public void Several_alerts_come_back_most_urgent_first()
    {
        var p3 = Item(priority: Priority.P3);
        var p1 = Item(priority: Priority.P1);
        var p2 = Item(priority: Priority.P2);

        var alerts = _scheduler.Tick([p3, p1, p2], Now, Free).Alerts;

        Assert.Equal([p1.Id, p2.Id, p3.Id], alerts.Select(a => a.Id));
    }

    [Fact]
    public void Busy_meeting_defers_alerts_and_toasts_until_it_ends()
    {
        var result = _scheduler.Tick([Item(Interrupt.Alert), Item(Interrupt.Toast)], Now, InMeeting());

        Assert.True(result.IsEmpty);
        Assert.All(result.Deferred, d => Assert.Equal(Now.AddMinutes(30), d.UntilUtc));
    }

    [Fact]
    public void Busy_meeting_defers_only_until_the_item_is_due()
    {
        var d = Assert.Single(_scheduler.Tick([Item(due: Now.AddMinutes(5))], Now, InMeeting()).Deferred);
        Assert.Equal(Now.AddMinutes(5), d.UntilUtc);
    }

    [Fact]
    public void Item_already_due_fires_through_a_busy_meeting() =>
        Assert.Single(_scheduler.Tick([Item(due: Now)], Now, InMeeting()).Alerts);

    [Theory]
    [InlineData(ShowAs.Free)]
    [InlineData(ShowAs.Tentative)]   // includes rNotice's own promoted blocks
    [InlineData(ShowAs.WorkingElsewhere)]
    public void Only_busy_and_oof_suppress(ShowAs showAs) =>
        Assert.Single(_scheduler.Tick([Item()], Now, InMeeting(showAs)).Alerts);

    [Fact]
    public void Oof_suppresses() =>
        Assert.Single(_scheduler.Tick([Item()], Now, InMeeting(ShowAs.Oof)).Deferred);

    [Fact]
    public void Takeover_ignores_busy_and_quiet_hours()
    {
        var quiet = new Scheduler(new SchedulerOptions
        {
            QuietHours = new QuietHours(new TimeOnly(0, 0), new TimeOnly(23, 59), TimeZoneInfo.Utc)
        });

        Assert.Single(quiet.Tick([Item(Interrupt.Takeover, due: Now.AddSeconds(60))], Now, InMeeting()).Takeovers);
    }

    [Fact]
    public void Takeover_for_a_meeting_long_started_comes_up_as_an_alert()
    {
        var result = _scheduler.Tick([Item(Interrupt.Takeover, due: Now.AddMinutes(-10))], Now, Free);

        Assert.Empty(result.Takeovers);
        Assert.Single(result.Alerts);
    }

    [Fact]
    public void Takeover_still_fires_inside_the_grace_period() =>
        Assert.Single(_scheduler.Tick([Item(Interrupt.Takeover, due: Now.AddMinutes(-2))], Now, Free).Takeovers);

    [Fact]
    public void Snooze_holds_then_rearms()
    {
        var fired = Scheduler.MarkFired(Item(), Now.AddMinutes(-5));
        var snoozed = fired.Snooze(Now.AddMinutes(10));

        Assert.True(_scheduler.Tick([snoozed], Now, Free).IsEmpty);
        Assert.Single(_scheduler.Tick([snoozed], Now.AddMinutes(10), Free).Alerts);
    }

    [Fact]
    public void Quiet_hours_wrapping_midnight_defer_until_they_end()
    {
        // Mountain time, 22:00–07:00. 14:00Z on 21 Oct 2026 is 08:00 MDT — outside.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");
        var scheduler = new Scheduler(new SchedulerOptions
        {
            QuietHours = new QuietHours(new TimeOnly(22, 0), new TimeOnly(7, 0), zone)
        });

        Assert.Single(scheduler.Tick([Item()], Now, Free).Alerts);

        var lateNight = new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero);   // 23:00 MDT on the 21st
        var d = Assert.Single(scheduler.Tick([Item(fireAt: lateNight)], lateNight, Free).Deferred);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 13, 0, 0, TimeSpan.Zero), d.UntilUtc);   // 07:00 MDT
    }
}
