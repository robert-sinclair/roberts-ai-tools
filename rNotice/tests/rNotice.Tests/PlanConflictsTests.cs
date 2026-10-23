using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using static rNotice.Tests.Make;

namespace rNotice.Tests;

public class PlanConflictsTests
{
    private static AgendaEntry Meeting(int fromMin, int toMin, string title = "ASU / UoO DB index and stats rebuild") =>
        new(Now.AddMinutes(fromMin), Now.AddMinutes(toMin), title, IsMeeting: true);

    private static Item Task(int startMin, int minutes = 30) => Item(stage: Stage.Intake).Plan(Now.AddMinutes(startMin), minutes);

    [Fact]
    public void A_meeting_booked_onto_a_planned_slot_is_a_conflict()
    {
        var task = Task(90);   // 15:30-16:00
        var found = PlanConflicts.Find([task], [Meeting(90, 120)], Now, 30);
        Assert.Equal(task.Id, Assert.Single(found).Task.Id);
    }

    [Fact]
    public void Touching_is_not_overlapping() =>
        Assert.Empty(PlanConflicts.Find([Task(60)], [Meeting(30, 60), Meeting(90, 120)], Now, 30));

    [Fact]
    public void A_slot_already_over_is_left_alone() =>
        Assert.Empty(PlanConflicts.Find([Task(-60)], [Meeting(-60, -30)], Now, 30));

    [Fact]
    public void Meetings_themselves_and_untimed_plans_are_never_conflicts()
    {
        var meetingItem = Item(stage: Stage.Planned) with { Source = Source.Calendar, PlannedStartUtc = Now.AddMinutes(90) };
        var untimed = Item(stage: Stage.Intake).Plan(null);
        Assert.Empty(PlanConflicts.Find([meetingItem, untimed], [Meeting(0, 600)], Now, 30));
    }

    [Fact]
    public void Back_to_Intake_clears_the_plan_and_says_why()
    {
        var task = Task(90);
        var cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
        var note = PlanConflicts.Note(task, Meeting(90, 120), cst, 30);
        var moved = task.BackToIntake(note, Now);

        Assert.Equal(Stage.Intake, moved.Stage);
        Assert.Null(moved.PlannedStartUtc);
        Assert.Equal("Moved back to Intake: its Wed 21 Oct 09:30–10:00 slot now clashes with \"ASU / UoO DB index and stats rebuild\".", moved.Note);
        Assert.Null(moved.Plan(Now.AddDays(1), 30).Note);   // planning again clears the note
    }
}

public class DayLayoutTests
{
    private sealed record B(string Name, int From, int To);

    private static IReadOnlyList<DayLayout.Placed<B>> Arrange(params B[] blocks) =>
        DayLayout.Arrange(blocks, b => Now.AddMinutes(b.From), b => Now.AddMinutes(b.To));

    [Fact]
    public void A_block_alone_takes_the_full_width() =>
        Assert.Equal(1, Assert.Single(Arrange(new B("a", 0, 30))).Columns);

    [Fact]
    public void Overlapping_blocks_share_the_width_side_by_side()
    {
        var placed = Arrange(new B("a", 0, 60), new B("b", 30, 90));
        Assert.All(placed, p => Assert.Equal(2, p.Columns));
        Assert.Equal([0, 1], placed.Select(p => p.Column));
    }

    [Fact]
    public void A_column_frees_up_once_its_block_ends()
    {
        // a 0-60, b 30-90, c 60-120: c reuses a's column.
        var placed = Arrange(new B("a", 0, 60), new B("b", 30, 90), new B("c", 60, 120)).ToDictionary(p => p.Block.Name);
        Assert.Equal(0, placed["c"].Column);
        Assert.Equal(2, placed["c"].Columns);
    }

    [Fact]
    public void Separate_clusters_are_laid_out_independently()
    {
        var placed = Arrange(new B("a", 0, 30), new B("b", 0, 30), new B("c", 120, 150)).ToDictionary(p => p.Block.Name);
        Assert.Equal(2, placed["a"].Columns);
        Assert.Equal(1, placed["c"].Columns);
    }
}
