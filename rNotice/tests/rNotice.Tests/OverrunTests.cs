using rNotice.Core.Items;
using rNotice.Core.Scheduling;
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

    private static Item Task(string title, int hour, int minute, int minutes) =>
        (Item(Interrupt.Silent, Priority.P3) with { Title = title, Source = Source.Teams, Id = Guid.NewGuid() })
            .Plan(T(hour, minute), minutes);

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

    [Fact]
    public void The_check_in_comes_no_later_than_the_next_plan_s_alert()
    {
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);                 // block ends 10:00
        var b = Task("B", 10, 0, 30).Plan(T(10), 30, true, alertLeadMinutes: 5);   // alerts 9:55

        Assert.Equal(T(9, 55), Overrun.CheckIn([a, b], a, a.FireAtUtc!.Value));
        Assert.Equal(T(10), Overrun.CheckIn([a], a, a.FireAtUtc!.Value));
    }

    [Fact]
    public void While_working_the_next_plan_waits_for_the_check_in_instead_of_alerting_too()
    {
        var scheduler = new Scheduler(new SchedulerOptions());
        var a = Task("A", 9, 0, 60).StartWork(T(9, 5), 30);   // check-in 10:00
        var b = Task("B", 10, 0, 30);                          // its own alert also 10:00

        var result = scheduler.Tick([a, b], T(10), []);

        Assert.Equal(["A"], result.Fired.Select(i => i.Title));
    }

    [Fact]
    public void Once_you_stop_working_the_next_plan_alerts_as_usual()
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
