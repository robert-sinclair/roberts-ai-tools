using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using rNotice.Core.Sources;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

/// <summary>One pop-up (P2.6): meetings and tasks planned with a time, at T−1; nothing else.</summary>
public class SchedulerTests
{
    private static readonly IReadOnlyList<BusyBlock> Free = [];
    private readonly Scheduler _scheduler = new(new SchedulerOptions());

    private static IReadOnlyList<BusyBlock> InMeeting(ShowAs showAs = ShowAs.Busy, int endsInMinutes = 30) =>
        [new BusyBlock(Now.AddMinutes(-10), Now.AddMinutes(endsInMinutes), showAs)];

    /// <summary>A quiet Teams message or mail, which you then planned for a time.</summary>
    private static Item PlannedQuiet(DateTimeOffset start) =>
        (Item(Interrupt.Silent, Priority.P3) with { Source = Source.Teams }).Plan(start, 30, alertLeadMinutes: 5);

    private static Item Meeting(DateTimeOffset start) =>
        Item(Interrupt.Takeover, fireAt: start.AddSeconds(-60), due: start, stage: Stage.Planned) with { Source = Source.Calendar };

    [Fact]
    public void A_planned_task_pops_up_a_minute_before_its_start_whatever_its_alert_lead_said()
    {
        var task = PlannedQuiet(Now.AddMinutes(10));   // planned with the old 5-minute lead

        Assert.True(_scheduler.Tick([task], Now.AddMinutes(8), Free).IsEmpty);
        Assert.True(_scheduler.Tick([task], Now.AddMinutes(9).AddSeconds(-1), Free).IsEmpty);
        Assert.Single(_scheduler.Tick([task], Now.AddMinutes(9), Free).Takeovers);
    }

    [Fact]
    public void A_meeting_pops_up_at_its_takeover_time()
    {
        Assert.True(_scheduler.Tick([Meeting(Now.AddMinutes(2))], Now, Free).IsEmpty);
        Assert.Single(_scheduler.Tick([Meeting(Now.AddSeconds(60))], Now, Free).Takeovers);
    }

    [Fact]
    public void A_plan_found_long_after_its_start_still_pops_up() =>
        Assert.Single(_scheduler.Tick([PlannedQuiet(Now.AddMinutes(-30))], Now, Free).Takeovers);

    [Fact]
    public void Nothing_else_pops_up()
    {
        var result = _scheduler.Tick(
        [
            Item(Interrupt.Takeover, due: Now.AddSeconds(30)),                         // a workbook row set to take over
            Item(Interrupt.Alert, Priority.P1) with { Source = Source.Teams },          // the routine's P1
            Item(Interrupt.Toast) with { Source = Source.Mail },                        // a reply on your thread
            Item(Interrupt.Alert, Priority.P1) with { Source = Source.Mail },           // VIP mail
            Item(Interrupt.Alert, stage: Stage.Planned),                                // planned, no time
        ], Now, Free);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Closed_acked_already_fired_and_grouped_plans_do_not_pop_up()
    {
        var task = PlannedQuiet(Now);
        var result = _scheduler.Tick(
        [
            task with { Stage = Stage.Done },
            task with { Stage = Stage.Dismissed },
            task with { AckedAtUtc = Now.AddMinutes(-2) },
            task with { LastFiredUtc = Now.AddMinutes(-1) },
            task with { GroupId = Guid.NewGuid() },
        ], Now, Free);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Meetings_come_first_then_the_most_urgent()
    {
        var p3 = PlannedQuiet(Now) with { Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.Rule, "test") };
        var p1 = PlannedQuiet(Now) with { Classification = new Classification(Priority.P1, Interrupt.Silent, ClassifiedBy.Rule, "test") };
        var meeting = Meeting(Now.AddSeconds(30));

        Assert.Equal([meeting.Id, p1.Id, p3.Id], _scheduler.Tick([p3, p1, meeting], Now, Free).Takeovers.Select(t => t.Id));
    }

    [Fact]
    public void Busy_time_and_quiet_hours_hold_nothing()
    {
        var quiet = new Scheduler(new SchedulerOptions
        {
            QuietHours = new QuietHours(new TimeOnly(0, 0), new TimeOnly(23, 59), TimeZoneInfo.Utc)
        });

        Assert.Single(quiet.Tick([PlannedQuiet(Now.AddSeconds(60))], Now, InMeeting()).Takeovers);
    }

    [Fact]
    public void A_meeting_snoozed_to_just_before_its_start_comes_back()
    {
        var start = Now.AddSeconds(60);
        var snoozed = Scheduler.MarkFired(Meeting(start), Now).Snooze(start.AddSeconds(-10));

        Assert.Empty(_scheduler.Tick([snoozed], start.AddSeconds(-11), Free).Takeovers);
        Assert.Single(_scheduler.Tick([snoozed], start.AddSeconds(-10), Free).Takeovers);
    }

    [Fact]
    public void Meeting_long_started_stays_in_the_queue_without_a_window() =>
        Assert.True(_scheduler.Tick([Meeting(Now.AddMinutes(-10))], Now, Free).IsEmpty);

    [Fact]
    public void Meeting_still_pops_up_inside_the_grace_period() =>
        Assert.Single(_scheduler.Tick([Meeting(Now.AddMinutes(-2))], Now, Free).Takeovers);

    [Fact]
    public void Snooze_holds_a_plan_then_rearms()
    {
        var snoozed = Scheduler.MarkFired(PlannedQuiet(Now.AddMinutes(-5)), Now.AddMinutes(-5)).Snooze(Now.AddMinutes(10));

        Assert.True(_scheduler.Tick([snoozed], Now, Free).IsEmpty);
        Assert.Single(_scheduler.Tick([snoozed], Now.AddMinutes(10), Free).Takeovers);
    }

    private static Item LiveMeeting(DateTimeOffset? lastFired = null, DateTimeOffset? joined = null) =>
        Item(Interrupt.Takeover, due: Now.AddMinutes(-10), stage: Stage.Planned, sourceId: "ev-1") with
        { Source = Source.Calendar, LastFiredUtc = lastFired, JoinedUtc = joined };

    private static readonly HashSet<string> Others = ["ev-1"];

    [Fact]
    public void A_meeting_with_others_you_havent_joined_takes_over_every_minute()
    {
        Assert.Single(_scheduler.Tick([LiveMeeting()], Now, Free, Others).Takeovers);
        Assert.Single(_scheduler.Tick([LiveMeeting(lastFired: Now.AddSeconds(-60))], Now, Free, Others).Takeovers);
        Assert.True(_scheduler.Tick([LiveMeeting(lastFired: Now.AddSeconds(-30))], Now, Free, Others).IsEmpty);
    }

    [Fact]
    public void Joining_snoozing_or_marking_done_stops_the_repeat()
    {
        Assert.True(_scheduler.Tick([LiveMeeting(joined: Now.AddMinutes(-5))], Now, Free, Others).IsEmpty);
        Assert.True(_scheduler.Tick([LiveMeeting() with { SnoozedUntilUtc = Now.AddMinutes(2) }], Now, Free, Others).IsEmpty);
        Assert.True(_scheduler.Tick([LiveMeeting() with { Stage = Stage.Done }], Now, Free, Others).IsEmpty);
    }

    [Fact]
    public void A_meeting_only_you_are_in_doesnt_repeat() =>
        Assert.True(_scheduler.Tick([LiveMeeting(lastFired: Now.AddMinutes(-11))], Now, Free, new HashSet<string>()).IsEmpty);

    [Fact]
    public void A_meeting_not_started_yet_doesnt_repeat() =>
        Assert.True(_scheduler.Tick([LiveMeeting(lastFired: Now.AddMinutes(-2)) with { DueUtc = Now.AddMinutes(1) }], Now, Free, Others).IsEmpty);
}
