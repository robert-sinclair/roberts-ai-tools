namespace rNotice.Core.Scheduling;

/// <summary>Something occupying time on a day: a meeting, or work you've already planned.</summary>
public sealed record AgendaEntry(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Title, bool IsMeeting, string? EventId = null);

public enum CellState
{
    Free,

    /// <summary>A meeting is on.</summary>
    Meeting,

    /// <summary>Something else you planned with rNotice.</summary>
    Planned,

    /// <summary>Already gone.</summary>
    Past
}

/// <summary>One quarter hour of the working day. <see cref="Label"/> is set where an entry starts.</summary>
public sealed record DayCell(DateTimeOffset StartUtc, CellState State, string? Label);

/// <summary>
/// A working day as quarter-hour cells, for the Plan dialog's day view: what's free, what's
/// booked and by what, what's already past. Same rules as <see cref="FreeSlots"/>, so a cell
/// shown free is one the suggestions could have offered.
/// </summary>
public static class DayGrid
{
    public static IReadOnlyList<DayCell> Build(DateOnly day, WorkHours hours, IEnumerable<AgendaEntry> agenda, DateTimeOffset nowUtc)
    {
        var entries = agenda.ToList();
        var cells = new List<DayCell>();
        var firstFree = FreeSlots.RoundUp(nowUtc, hours.Zone);
        var labelled = new HashSet<AgendaEntry>();

        for (var t = day.ToDateTime(hours.Start); t < day.ToDateTime(hours.End); t = t.AddMinutes(15))
        {
            var start = new DateTimeOffset(t, hours.Zone.GetUtcOffset(t)).ToUniversalTime();
            var end = start + FreeSlots.Step;

            var here = entries.Where(e => e.StartUtc < end && e.EndUtc > start).ToList();
            var meeting = here.FirstOrDefault(e => e.IsMeeting);
            var planned = here.FirstOrDefault(e => !e.IsMeeting);
            var shown = meeting ?? planned;

            var state = start < firstFree ? CellState.Past
                : meeting is not null ? CellState.Meeting
                : planned is not null ? CellState.Planned
                : CellState.Free;

            // Name an entry once, on the first cell of it that's visible today.
            string? label = null;
            if (shown is not null && labelled.Add(shown)) label = shown.Title;

            cells.Add(new DayCell(start, state, label));
        }
        return cells;
    }

    /// <summary>
    /// The cells from <paramref name="from"/> to <paramref name="to"/> (either order), or as far
    /// as they stay free — a selection can't run through a meeting or another plan.
    /// </summary>
    public static (int First, int Last) Selection(IReadOnlyList<DayCell> cells, int from, int to)
    {
        if (cells[from].State != CellState.Free) return (-1, -1);
        var step = to >= from ? 1 : -1;
        var last = from;
        for (var i = from + step; step > 0 ? i <= to : i >= to; i += step)
        {
            if (cells[i].State != CellState.Free) break;
            last = i;
        }
        return step > 0 ? (from, last) : (last, from);
    }
}
