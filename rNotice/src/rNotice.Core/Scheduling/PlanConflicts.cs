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

    /// <summary>The words before the meeting's name, which the queue shows as a link.</summary>
    public const string ClashedWith = " clashed with ";

    /// <summary>
    /// "Moved back: 11:30 clashed with ASU / UoO …" — short, because it sits in the Intake list.
    /// The day is added only when the slot wasn't today. The meeting's name ends the note so the
    /// queue can make it a link to the meeting.
    /// </summary>
    public static string Note(Item task, AgendaEntry meeting, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        var start = TimeZoneInfo.ConvertTime(task.PlannedStartUtc!.Value, zone);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var when = start.Date == today
            ? start.ToString("HH:mm", CultureInfo.InvariantCulture)
            : start.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        return $"Moved back: {when}{ClashedWith}{meeting.Title}";
    }

    // 0.6.0 wrote: Moved back to Intake: its Fri 23 Oct 11:30–12:00 slot now clashes with "Title".
    private static readonly System.Text.RegularExpressions.Regex Long = new(
        """^Moved back to Intake: its (?<day>\w{3}) \d{1,2} \w{3} (?<time>\d{2}:\d{2})–\d{2}:\d{2} slot now clashes with "(?<title>.+)"\.$""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Rewrites a note in the long form 0.6.0 used into the short one, and gives back the meeting
    /// title so the caller can find the meeting to link. False if it isn't one of those notes.
    /// </summary>
    public static bool TryShorten(string? note, out string shortNote, out string title)
    {
        shortNote = title = "";
        if (note is null || Long.Match(note) is not { Success: true } m) return false;
        title = m.Groups["title"].Value;
        shortNote = $"Moved back: {m.Groups["day"].Value} {m.Groups["time"].Value}{ClashedWith}{title}";
        return true;
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
