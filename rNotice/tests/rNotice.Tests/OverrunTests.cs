using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using rNotice.Core.Sources;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class OverrunTests
{
    // Fixed UTC-6, no DST. Thu 22 Oct 2026.
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
    private static readonly WorkHours Hours = new(new TimeOnly(8, 0), new TimeOnly(16, 0), Cst,
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday });

    private static DateTimeOffset T(int hour, int minute = 0, int day = 22) =>
        new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static Item Task(string title, int hour, int minute, int minutes, int day = 22) =>
        (Item(Interrupt.Silent, Priority.P3) with { Title = title, Source = Source.Teams, Id = Guid.NewGuid() })
            .Plan(T(hour, minute, day), minutes);

    private static Dictionary<string, DateTimeOffset> Moves(IEnumerable<Push> pushes) =>
        pushes.ToDictionary(p => p.Task.Title, p => p.StartUtc);

    [Fact]
    public void Robert_s_example_A_runs_over_30_min_and_B_and_C_move_along()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9), 30);
        var b = Task("B", 10, 0, 30);
        var c = Task("C", 10, 30, 30);

        var moves = Moves(Overrun.Pushes([a, b, c], a, T(10, 30), Hours, [], 30));

        Assert.Equal(new Dictionary<string, DateTimeOffset> { ["B"] = T(10, 30), ["C"] = T(11, 0) }, moves);
    }

    [Fact]
    public void A_plan_with_room_to_spare_stays_and_so_does_everything_after_it()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9), 30);
        var b = Task("B", 10, 0, 30);
        var c = Task("C", 11, 0, 30);   // 10:30-11 is free: B fits there and C needn't move

        Assert.Equal(["B"], Moves(Overrun.Pushes([a, b, c], a, T(10, 15), Hours, [], 30)).Keys);
    }

    [Fact]
    public void A_pushed_plan_jumps_a_meeting()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9), 30);
        var b = Task("B", 10, 0, 30);
        var standup = new TimeBlock(T(10, 30), T(11, 0));

        Assert.Equal(T(11, 0), Moves(Overrun.Pushes([a, b], a, T(10, 30), Hours, [standup], 30))["B"]);
    }

    [Fact]
    public void A_plan_pushed_past_the_end_of_the_day_goes_to_the_next_working_day()
    {
        var a = Task("A", 15, 0, 30).StartWork(T(15), 30);
        var b = Task("B", 15, 30, 30);

        Assert.Equal(T(8, 0, day: 23), Moves(Overrun.Pushes([a, b], a, T(15, 45), Hours, [], 30))["B"]);
    }

    [Fact]
    public void Earlier_plans_meetings_and_unplanned_work_are_left_alone()
    {
        var earlier = Task("Earlier", 8, 0, 30);
        var a = Task("A", 9, 0, 60).StartWork(T(9), 30);
        var meeting = (Item(Interrupt.Takeover, Priority.P1) with { Source = Source.Calendar, Id = Guid.NewGuid() }).Plan(T(10), 30);

        Assert.Empty(Overrun.Pushes([earlier, a, meeting], a, T(10, 30), Hours, [], 30));
    }

    [Fact]
    public void Next_up_is_the_plan_starting_when_the_block_ends()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);
        var b = Task("B", 10, 0, 30);
        var later = Task("Later", 14, 0, 30);

        Assert.Equal("B", Overrun.NextUp([a, b, later], a, 30)?.Title);
        Assert.Null(Overrun.NextUp([a, later], a, 30));
    }

    // ── Check-ins (P2.6): only when nothing is booked right after the block ─────────────────

    [Fact]
    public void A_check_in_comes_at_the_end_of_the_block_when_nothing_is_next()
    {
        var scheduler = new Scheduler(new SchedulerOptions());
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);   // check-in 10:00
        var later = Task("Later", 14, 0, 30);

        Assert.True(scheduler.Tick([a, later], T(9, 59), []).IsEmpty);
        Assert.Equal(["A"], scheduler.Tick([a, later], T(10), []).Takeovers.Select(i => i.Title));
    }

    [Fact]
    public void With_a_plan_booked_right_after_there_is_no_check_in_and_the_session_ends()
    {
        var scheduler = new Scheduler(new SchedulerOptions());
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);
        var b = Task("B", 10, 0, 30);

        // B pops up at its own T-1, while A is still being worked on.
        Assert.Equal(["B"], scheduler.Tick([a, b], T(9, 59), []).Takeovers.Select(i => i.Title));

        var atEnd = scheduler.Tick([a, Scheduler.MarkFired(b, T(9, 59))], T(10), []);
        Assert.Empty(atEnd.Takeovers);
        Assert.Equal(["A"], atEnd.Ended.Select(i => i.Title));

        var stopped = Scheduler.EndSession(a, T(10));
        Assert.False(stopped.IsWorking);
        Assert.Equal(Stage.Planned, stopped.Stage);
        Assert.True(scheduler.Tick([stopped], T(10, 1), []).IsEmpty);
    }

    [Fact]
    public void A_meeting_right_after_also_counts_but_a_free_one_doesnt()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);
        var standup = new BusyBlock(T(10), T(10, 15), ShowAs.Busy);

        Assert.True(Scheduler.BookedAfter(a, [a], [standup]));
        Assert.False(Scheduler.BookedAfter(a, [a], [standup with { ShowAs = ShowAs.Free }]));
        Assert.False(Scheduler.BookedAfter(a, [a], [new BusyBlock(T(10, 30), T(11), ShowAs.Busy)]));   // a gap after
    }

    [Fact]
    public void Once_you_stop_working_the_next_plan_pops_up_as_usual()
    {
        var scheduler = new Scheduler(new SchedulerOptions());
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30).StopWork(T(10)) with { Stage = Stage.Done };
        var b = Task("B", 10, 0, 30);

        Assert.Equal(["B"], scheduler.Tick([a, b], T(10), []).Fired.Select(i => i.Title));
    }

    [Fact]
    public void Running_over_grows_the_block_and_moves_the_check_in()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30).RunOver(T(10, 30), T(10, 30));

        Assert.Equal((90, T(10, 30), T(9)), (a.PlannedMinutes!.Value, a.FireAtUtc!.Value, a.PlannedStartUtc!.Value));
        Assert.True(a.IsWorking);
    }
}
