using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

public sealed record Deferral(Item Item, DateTimeOffset UntilUtc, string Reason);

/// <summary>
/// What should reach the user right now, grouped by how. Several items at one level go up as
/// one window with a list, not a stack — the grouping is here so the UI can't get it wrong.
/// Each list is most urgent first.
/// </summary>
public sealed record TickResult(
    IReadOnlyList<Item> Takeovers,
    IReadOnlyList<Item> Alerts,
    IReadOnlyList<Item> Toasts,
    IReadOnlyList<Deferral> Deferred)
{
    public bool IsEmpty => Takeovers.Count == 0 && Alerts.Count == 0 && Toasts.Count == 0;
    public IEnumerable<Item> Fired => Takeovers.Concat(Alerts).Concat(Toasts);
}

/// <summary>
/// Decides what fires, when, and how loudly. Pure: everything it needs is passed in, and it
/// holds no state between ticks.
///
/// Firing is computed from the local store, never from a poll. A network outage delays seeing
/// new work; it never delays an alert already known about. That is the property this class
/// exists to protect.
///
/// The caller shows what comes back, then stores each fired item with
/// <see cref="MarkFired"/> so it doesn't fire again. Deferred items aren't marked; they come
/// back on a later tick once the suppression lifts.
/// </summary>
public sealed class Scheduler(SchedulerOptions options)
{
    private readonly SchedulerOptions _options = options;

    public TickResult Tick(IEnumerable<Item> items, DateTimeOffset nowUtc, IReadOnlyList<BusyBlock> calendar)
    {
        var takeovers = new List<Item>();
        var alerts = new List<Item>();
        var toasts = new List<Item>();
        var deferred = new List<Deferral>();

        foreach (var item in items)
        {
            if (!IsDue(item, nowUtc)) continue;

            var interrupt = EffectiveInterrupt(item, nowUtc);

            // Takeover ignores busy and quiet hours. It is mostly about meetings, so suppressing
            // it during meetings would defeat it.
            if (interrupt != Interrupt.Takeover && Suppression(item, nowUtc, calendar) is { } d)
            {
                deferred.Add(d);
                continue;
            }

            (interrupt switch
            {
                Interrupt.Takeover => takeovers,
                Interrupt.Alert => alerts,
                _ => toasts
            }).Add(item);
        }

        return new TickResult(Order(takeovers), Order(alerts), Order(toasts), deferred);
    }

    public static Item MarkFired(Item item, DateTimeOffset nowUtc) => item with { LastFiredUtc = nowUtc };

    private static bool IsDue(Item item, DateTimeOffset nowUtc)
    {
        if (!item.IsOpen || item.AckedAtUtc is not null || item.LastFiredUtc is not null) return false;
        if (item.Classification.Interrupt == Interrupt.Silent) return false;
        if (item.FireAtUtc is not { } fireAt) return false;

        var effective = item.SnoozedUntilUtc is { } snooze && snooze > fireAt ? snooze : fireAt;
        return effective <= nowUtc;
    }

    private Interrupt EffectiveInterrupt(Item item, DateTimeOffset nowUtc)
    {
        var interrupt = item.Classification.Interrupt;
        if (interrupt == Interrupt.Takeover && item.DueUtc is { } start &&
            nowUtc > start.AddSeconds(_options.TakeoverGraceSeconds))
        {
            return Interrupt.Alert;
        }
        return interrupt;
    }

    /// <summary>
    /// Quiet hours hold until they end. A busy meeting holds until it ends or the item falls due,
    /// whichever is first — so something due mid-meeting still fires on time.
    /// </summary>
    private Deferral? Suppression(Item item, DateTimeOffset nowUtc, IReadOnlyList<BusyBlock> calendar)
    {
        if (_options.QuietHours is { } quiet && quiet.Contains(nowUtc))
            return new Deferral(item, quiet.NextEnd(nowUtc), "Quiet hours");

        if (item.DueUtc is { } due && due <= nowUtc) return null;

        var busy = calendar.Where(b => b.Suppresses && b.Covers(nowUtc))
                           .Select(b => (DateTimeOffset?)b.EndUtc)
                           .Max();
        if (busy is not { } busyUntil) return null;

        var until = item.DueUtc is { } d && d < busyUntil ? d : busyUntil;
        return new Deferral(item, until, "In a meeting");
    }

    private static List<Item> Order(List<Item> items) =>
        items.OrderBy(i => i.Classification.Priority)
             .ThenBy(i => i.DueUtc ?? i.FireAtUtc)
             .ToList();
}
