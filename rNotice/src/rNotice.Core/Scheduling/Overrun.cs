using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>A planned task that has to move because the one before it ran over.</summary>
public sealed record Push(Item Task, DateTimeOffset StartUtc);

/// <summary>
/// Running over on a task you're working on. Asked 2026-10-27: with A 9–10, B 10–10:30 and
/// C 10:30–11, keeping going on A for 30 min at 10:00 makes A 9–10:30, B 10:30–11 and C 11–11:30.
///
/// The task's block grows to the new end. Each later plan that the grown block (or a plan
/// already pushed) now lands on moves to the first free slot after it, in the order they were
/// planned: on the quarter hour, inside work hours, clear of meetings — so a push can jump a
/// meeting or go to the next working day. A plan with room to spare stays put, and so does
/// everything after it unless something pushed lands on it. Pure.
/// </summary>
public static class Overrun
{
    /// <summary>The plans that move if <paramref name="working"/>'s block ends at <paramref name="newEndUtc"/>.</summary>
    public static IReadOnlyList<Push> Pushes(
        IEnumerable<Item> items, Item working, DateTimeOffset newEndUtc,
        WorkHours hours, IEnumerable<TimeBlock> meetings, int defaultMinutes)
    {
        if (working.PlannedStartUtc is not { } start) return [];

        var meetingBlocks = meetings.ToList();
        var placed = new List<TimeBlock> { new(start, newEndUtc) };
        var pushes = new List<Push>();

        foreach (var task in Later(items, working, start).OrderBy(t => t.PlannedStartUtc))
        {
            var from = task.PlannedStartUtc!.Value;
            var length = TimeSpan.FromMinutes(task.PlannedMinutes ?? defaultMinutes);

            if (!placed.Any(b => b.Overlaps(from, from + length)))
            {
                placed.Add(new TimeBlock(from, from + length));
                continue;
            }

            // Only meetings and what's already been placed are in the way: a later plan that's
            // landed on gets pushed in its turn, so the order you planned them in is kept.
            if (FreeSlots.Find(from, length, hours, meetingBlocks.Concat(placed), max: 1) is not [var slot])
            {
                placed.Add(new TimeBlock(from, from + length));   // nowhere to go: leave it
                continue;
            }
            pushes.Add(new Push(task, slot));
            placed.Add(new TimeBlock(slot, slot + length));
        }
        return pushes;
    }

    /// <summary>
    /// The plan that's next once <paramref name="working"/>'s time is up: the first not yet
    /// raised that starts between its start and its check-in (or the end of its block, if later).
    /// That's the one the check-in offers to start instead.
    /// </summary>
    public static Item? NextUp(IEnumerable<Item> items, Item working, int defaultMinutes)
    {
        var from = working.PlannedStartUtc ?? working.WorkStartedUtc;
        if (from is null) return null;
        var blockEnd = working.PlannedStartUtc?.AddMinutes(working.PlannedMinutes ?? defaultMinutes);
        var until = Max(blockEnd, working.FireAtUtc);
        return Later(items, working, from.Value)
            .Where(t => t.LastFiredUtc is null && t.PlannedStartUtc <= until)
            .OrderBy(t => t.PlannedStartUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// When to check in: <paramref name="checkInUtc"/>, or sooner if a later plan's alert comes
    /// first — the check-in is the hand-off to it. While you're working, that plan's own alert
    /// waits (<see cref="Scheduler"/>), so you get one prompt, not two.
    /// </summary>
    public static DateTimeOffset CheckIn(IEnumerable<Item> items, Item working, DateTimeOffset checkInUtc)
    {
        var from = working.PlannedStartUtc ?? working.WorkStartedUtc ?? checkInUtc;
        var next = Later(items, working, from)
            .Where(t => t.LastFiredUtc is null && t.FireAtUtc is not null)
            .Select(t => t.FireAtUtc!.Value)
            .Where(f => f > (working.WorkStartedUtc ?? DateTimeOffset.MinValue))
            .DefaultIfEmpty(checkInUtc)
            .Min();
        return next < checkInUtc ? next : checkInUtc;
    }

    /// <summary>Other open plans with a time, starting at or after <paramref name="fromUtc"/>.</summary>
    private static IEnumerable<Item> Later(IEnumerable<Item> items, Item working, DateTimeOffset fromUtc) =>
        items.Where(t => t.Id != working.Id && t.Stage == Stage.Planned && t.Source != Source.Calendar &&
                         !t.IsWorking && t.PlannedStartUtc is { } s && s >= fromUtc);

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
