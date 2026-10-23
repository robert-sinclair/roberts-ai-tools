using System.Globalization;
using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>
/// Planned tasks whose slot a meeting now occupies — a meeting booked or moved onto it after you
/// planned. Those go back to Intake with a note; rNotice doesn't silently keep a plan that
/// double-books you.
/// </summary>
public static class PlanConflicts
{
    /// <summary>
    /// Each open planned task (not a meeting) with a time, whose slot hasn't finished, paired with
    /// the first meeting that overlaps it.
    /// </summary>
    public static IReadOnlyList<(Item Task, AgendaEntry Meeting)> Find(
        IEnumerable<Item> items, IEnumerable<AgendaEntry> meetings, DateTimeOffset nowUtc, int defaultMinutes)
    {
        var busy = meetings.Where(m => m.IsMeeting).OrderBy(m => m.StartUtc).ToList();
        var found = new List<(Item, AgendaEntry)>();

        foreach (var item in items)
        {
            if (item.Stage != Stage.Planned || item.Source == Source.Calendar || item.PlannedStartUtc is not { } start)
                continue;

            var end = start.AddMinutes(item.PlannedMinutes ?? defaultMinutes);
            if (end <= nowUtc) continue;   // already past: nothing to rescue

            if (busy.FirstOrDefault(m => m.StartUtc < end && m.EndUtc > start) is { } clash)
                found.Add((item, clash));
        }
        return found;
    }

    /// <summary>"Moved back to Intake: Fri 23 Oct 11:30–12:00 now clashes with "ASU / UoO …"."</summary>
    public static string Note(Item task, AgendaEntry meeting, TimeZoneInfo zone, int defaultMinutes)
    {
        var start = TimeZoneInfo.ConvertTime(task.PlannedStartUtc!.Value, zone);
        var end = start.AddMinutes(task.PlannedMinutes ?? defaultMinutes);
        return string.Create(CultureInfo.InvariantCulture,
            $"Moved back to Intake: its {start:ddd d MMM HH:mm}–{end:HH:mm} slot now clashes with \"{meeting.Title}\".");
    }
}

/// <summary>
/// Side-by-side placement for a calendar day, as Outlook does it: blocks that overlap share the
/// width in columns; a block alone gets the full width.
/// </summary>
public static class DayLayout
{
    public sealed record Placed<T>(T Block, int Column, int Columns);

    public static IReadOnlyList<Placed<T>> Arrange<T>(IEnumerable<T> blocks, Func<T, DateTimeOffset> start, Func<T, DateTimeOffset> end)
    {
        var ordered = blocks.OrderBy(start).ThenByDescending(end).ToList();
        var placed = new List<Placed<T>>();

        // Work through clusters of blocks that overlap one another, directly or in a chain.
        var i = 0;
        while (i < ordered.Count)
        {
            var cluster = new List<T> { ordered[i] };
            var clusterEnd = end(ordered[i]);
            var j = i + 1;
            while (j < ordered.Count && start(ordered[j]) < clusterEnd)
            {
                cluster.Add(ordered[j]);
                if (end(ordered[j]) > clusterEnd) clusterEnd = end(ordered[j]);
                j++;
            }

            // Within the cluster, each block takes the first column that's free by its start.
            var columnEnds = new List<DateTimeOffset>();
            var columns = new List<(T Block, int Column)>();
            foreach (var block in cluster)
            {
                var col = columnEnds.FindIndex(e => e <= start(block));
                if (col < 0) { col = columnEnds.Count; columnEnds.Add(end(block)); }
                else columnEnds[col] = end(block);
                columns.Add((block, col));
            }
            placed.AddRange(columns.Select(c => new Placed<T>(c.Block, c.Column, columnEnds.Count)));
            i = j;
        }
        return placed;
    }
}
