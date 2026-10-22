using rNotice.Core.Scheduling;

namespace rNotice.Tests;

public class DayGridTests
{
    private static readonly TimeZoneInfo Cst = TimeZoneInfo.FindSystemTimeZoneById("Canada Central Standard Time");
    private static readonly WorkHours Hours = new(new TimeOnly(8, 0), new TimeOnly(16, 0), Cst,
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday });
    private static readonly DateOnly Friday = new(2026, 10, 23);

    private static DateTimeOffset T(int h, int m = 0, int day = 23) =>
        new DateTimeOffset(2026, 10, day, h, m, 0, TimeSpan.FromHours(-6)).ToUniversalTime();

    private static int Index(int h, int m) => ((h - 8) * 60 + m) / 15;

    [Fact]
    public void A_working_day_is_32_quarter_hours()
    {
        var cells = DayGrid.Build(Friday, Hours, [], T(17, 0, day: 22));
        Assert.Equal(32, cells.Count);
        Assert.Equal(T(8, 0), cells[0].StartUtc);
        Assert.Equal(T(15, 45), cells[^1].StartUtc);
        Assert.All(cells, c => Assert.Equal(CellState.Free, c.State));
    }

    [Fact]
    public void Meetings_and_plans_fill_their_cells_and_are_named_once()
    {
        var cells = DayGrid.Build(Friday, Hours,
            [new AgendaEntry(T(9, 0), T(9, 30), "AI Pod Standup", true),
             new AgendaEntry(T(11, 0), T(11, 15), "Case 00038001", false)],
            T(17, 0, day: 22));

        Assert.Equal((CellState.Meeting, "AI Pod Standup"), (cells[Index(9, 0)].State, cells[Index(9, 0)].Label));
        Assert.Equal((CellState.Meeting, (string?)null), (cells[Index(9, 15)].State, cells[Index(9, 15)].Label));
        Assert.Equal(CellState.Free, cells[Index(9, 30)].State);
        Assert.Equal((CellState.Planned, "Case 00038001"), (cells[Index(11, 0)].State, cells[Index(11, 0)].Label));
    }

    [Fact]
    public void Earlier_today_is_past()
    {
        var cells = DayGrid.Build(Friday, Hours, [], T(10, 5));
        Assert.Equal(CellState.Past, cells[Index(10, 0)].State);
        Assert.Equal(CellState.Free, cells[Index(10, 15)].State);
    }

    [Fact]
    public void A_meeting_already_running_at_the_start_of_the_day_is_named_on_the_first_cell()
    {
        var cells = DayGrid.Build(Friday, Hours, [new AgendaEntry(T(7, 0), T(8, 30), "Early deploy", true)], T(17, 0, day: 22));
        Assert.Equal("Early deploy", cells[0].Label);
    }

    [Fact]
    public void Dragging_selects_a_run_and_stops_at_a_meeting()
    {
        var cells = DayGrid.Build(Friday, Hours, [new AgendaEntry(T(9, 0), T(9, 30), "Standup", true)], T(17, 0, day: 22));

        Assert.Equal((Index(8, 0), Index(8, 45)), DayGrid.Selection(cells, Index(8, 0), Index(10, 0)));
        Assert.Equal((Index(9, 30), Index(10, 0)), DayGrid.Selection(cells, Index(10, 0), Index(9, 30)));
        Assert.Equal((-1, -1), DayGrid.Selection(cells, Index(9, 0), Index(10, 0)));
    }
}
