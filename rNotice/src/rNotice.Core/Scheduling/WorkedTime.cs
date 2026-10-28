using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>
/// Time already spent in a planned block, kept when the plan moves on. <see cref="EventId"/> is
/// the Outlook event left behind as the record, or null for a plan that wasn't on the calendar.
/// </summary>
public sealed record WorkedBlock(string Id, Guid ItemId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? EventId);

/// <summary>
/// A plan and its Outlook event are no longer one-to-one for life (asked 2026-10-28). Once a
/// planned block has started, the time that's passed belongs to the record: rebooking, starting
/// something else, a meeting bouncing it back to Intake or dismissing it leaves that time in
/// place — on the schedule, and in Outlook if it was there, trimmed to now — and a new plan gets
/// an event of its own. Before the block starts, a plan moves or goes as it always has.
/// </summary>
public static class WorkedTime
{
    /// <summary>How far into a block counts as started. Under a minute, it hadn't really begun.</summary>
    public static readonly TimeSpan Started = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The span of <paramref name="before"/>'s block to keep, if this change leaves it behind:
    /// from its start to its end or now, whichever is first, on the minute. Null when nothing
    /// is left behind — not started, finished (Done keeps its whole block), or the same block
    /// carrying on (running over changes the end, not the start).
    /// </summary>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc)? Kept(Item before, Item after, DateTimeOffset nowUtc, int defaultMinutes)
    {
        if (before.Source == Source.Calendar || before.Stage != Stage.Planned || before.PlannedStartUtc is not { } start)
            return null;
        if (nowUtc - start < Started) return null;
        if (after.Stage == Stage.Done) return null;
        if (after.Stage == Stage.Planned && after.PlannedStartUtc == start) return null;

        var blockEnd = start.AddMinutes(before.PlannedMinutes ?? defaultMinutes);
        var now = new DateTimeOffset(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        return (start, now < blockEnd ? now : blockEnd);
    }
}
