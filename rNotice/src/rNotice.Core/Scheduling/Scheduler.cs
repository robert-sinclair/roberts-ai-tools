using rNotice.Core.Items;
using rNotice.Core.Sources;

namespace rNotice.Core.Scheduling;

public sealed record Deferral(Item Item, DateTimeOffset UntilUtc, string Reason);

/// <summary>
/// What should reach the user right now. Since P2.6 (2026-11-09) there is one kind of pop-up,
/// so everything that fires is in <see cref="Takeovers"/>; <see cref="Alerts"/>,
/// <see cref="Toasts"/> and <see cref="Deferred"/> stay empty and are kept for the callers.
/// <see cref="Ended"/> are working sessions whose check-in was skipped because something is
/// booked right after: the caller stops them.
/// </summary>
public sealed record TickResult(
    IReadOnlyList<Item> Takeovers,
    IReadOnlyList<Item> Alerts,
    IReadOnlyList<Item> Toasts,
    IReadOnlyList<Deferral> Deferred)
{
    public IReadOnlyList<Item> Ended { get; init; } = [];

    public bool IsEmpty => Takeovers.Count == 0 && Alerts.Count == 0 && Toasts.Count == 0 && Ended.Count == 0;
    public IEnumerable<Item> Fired => Takeovers.Concat(Alerts).Concat(Toasts);
}

/// <summary>
/// Decides what pops up, and when. Pure: everything it needs is passed in, and it holds no
/// state between ticks.
///
/// One pop-up (P2.6, Robert 2026-11-09), always the full one with the dim, and only for:
/// <list type="bullet">
/// <item>a meeting, at its takeover time (T−1);</item>
/// <item>a task planned with a time, at T−<see cref="SchedulerOptions.PopupLeadSeconds"/>;</item>
/// <item>a started meeting with others you haven't joined, every minute;</item>
/// <item>a check-in when a working block ends, unless something is booked right after it.
/// Then that event's own pop-up is the prompt, and the session is stopped instead.</item>
/// </list>
/// Nothing else pops up: mail, Teams chats (classified or not), @mentions and workbook rows
/// wait in Intake.
///
/// Firing is computed from the local store, never from a poll. A network outage delays seeing
/// new work; it never delays a pop-up already known about.
/// </summary>
public sealed class Scheduler(SchedulerOptions options)
{
    private readonly SchedulerOptions _options = options;

    /// <param name="calendar">Meetings, for "is something booked right after this block".</param>
    /// <param name="withOthers">
    /// Event ids of meetings with someone besides you in them. Once one of these has started and
    /// you haven't joined, it takes over again every <see cref="SchedulerOptions.NotJoinedRepeatSeconds"/>
    /// until you join, snooze, or mark it Done.
    /// </param>
    public TickResult Tick(IEnumerable<Item> items, DateTimeOffset nowUtc, IReadOnlyList<BusyBlock> calendar,
        IReadOnlySet<string>? withOthers = null)
    {
        var popups = new List<Item>();
        var ended = new List<Item>();
        var all = items as IReadOnlyCollection<Item> ?? items.ToList();

        foreach (var item in all)
        {
            if (withOthers is not null && NotJoined(item, nowUtc, withOthers))
            {
                popups.Add(item);
                continue;
            }
            if (PopUpAt(item) is not { } at || !IsOpenForPopUp(item)) continue;
            var effective = item.SnoozedUntilUtc is { } snooze && snooze > at ? snooze : at;
            if (effective > nowUtc) continue;

            if (item.IsWorking)
            {
                if (BookedAfter(item, all, calendar)) ended.Add(item);
                else popups.Add(item);
                continue;
            }

            // A meeting well past its start is under way: you're in it or you aren't, and a
            // window for it at startup is stale. (A meeting with others is handled above.)
            if (item.Source == Source.Calendar && item.DueUtc is { } start &&
                nowUtc > start.AddSeconds(_options.TakeoverGraceSeconds))
                continue;

            popups.Add(item);
        }

        return new TickResult(Order(popups), [], [], []) { Ended = ended };
    }

    public static Item MarkFired(Item item, DateTimeOffset nowUtc) => item with { LastFiredUtc = nowUtc };

    /// <summary>
    /// A working session whose check-in was skipped: stopped, time kept, the task left where it
    /// is. Marked fired so it doesn't come back.
    /// </summary>
    public static Item EndSession(Item item, DateTimeOffset nowUtc) => item.StopWork(nowUtc) with { LastFiredUtc = nowUtc };

    /// <summary>
    /// When an item pops up, or null if it never does: a meeting at its takeover time, a
    /// check-in at its time, a task planned with a time a minute before its start.
    /// </summary>
    public DateTimeOffset? PopUpAt(Item item)
    {
        if (item.Source == Source.Calendar) return item.Stage == Stage.Planned ? item.FireAtUtc : null;
        if (item.IsWorking) return item.FireAtUtc;
        if (item.Stage == Stage.Planned && item.GroupId is null && item.PlannedStartUtc is { } start)
            return start.AddSeconds(-_options.PopupLeadSeconds);
        return null;
    }

    private static bool IsOpenForPopUp(Item item) =>
        item.IsOpen && item.AckedAtUtc is null && item.LastFiredUtc is null;

    /// <summary>
    /// A meeting with others, started, not joined, not done: it takes over now if it hasn't in
    /// the last <see cref="SchedulerOptions.NotJoinedRepeatSeconds"/>. Ignores the grace period,
    /// which only stops a stale window at startup.
    /// </summary>
    private bool NotJoined(Item item, DateTimeOffset nowUtc, IReadOnlySet<string> withOthers) =>
        item.Source == Source.Calendar && item.Stage == Stage.Planned && item.IsOpen &&
        item.JoinedUtc is null && item.DueUtc <= nowUtc && withOthers.Contains(item.SourceId) &&
        !(item.SnoozedUntilUtc > nowUtc) &&
        (item.LastFiredUtc is not { } last || nowUtc - last >= TimeSpan.FromSeconds(_options.NotJoinedRepeatSeconds));

    /// <summary>
    /// Something takes the time right after a working block: a meeting not marked free, or
    /// another planned task, that starts within a minute of the block's end or is already
    /// running then.
    /// </summary>
    public static bool BookedAfter(Item working, IEnumerable<Item> items, IReadOnlyList<BusyBlock> calendar)
    {
        var end = working.PlannedStartUtc is { } s && working.PlannedMinutes is { } m ? s.AddMinutes(m) : working.FireAtUtc;
        if (end is not { } blockEnd) return false;
        var soon = blockEnd.AddMinutes(1);

        bool Takes(DateTimeOffset start, DateTimeOffset finish) => start <= soon && finish > blockEnd;

        return calendar.Any(b => b.ShowAs != ShowAs.Free && Takes(b.StartUtc, b.EndUtc)) ||
               items.Any(i => i.Id != working.Id && i.Stage == Stage.Planned && i.Source != Source.Calendar &&
                              i.GroupId is null && i.PlannedStartUtc is { } ps &&
                              Takes(ps, ps.AddMinutes(i.PlannedMinutes ?? 30)));
    }

    private static List<Item> Order(List<Item> items) =>
        items.OrderBy(i => i.Source == Source.Calendar ? 0 : 1)
             .ThenBy(i => i.Classification.Priority)
             .ThenBy(i => i.DueUtc ?? i.FireAtUtc)
             .ToList();
}
