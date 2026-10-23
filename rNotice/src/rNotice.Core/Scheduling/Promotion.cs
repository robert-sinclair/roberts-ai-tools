using rNotice.Core.Items;

namespace rNotice.Core.Scheduling;

/// <summary>An rNotice-tagged event as the calendar shows it now.</summary>
public sealed record PromotedEvent(string Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

public enum PromotionKind
{
    /// <summary>A plan that should be on the calendar and isn't yet.</summary>
    Create,

    /// <summary>You re-planned it here: move the event to the new time.</summary>
    Move,

    /// <summary>No longer wanted on the calendar — done, dismissed, back in Intake, no time, or removed.</summary>
    Remove,

    /// <summary>You moved the event in Outlook: the plan follows it.</summary>
    FollowOutlook,

    /// <summary>The event was deleted in Outlook: stop keeping one for this plan.</summary>
    Lost
}

public sealed record PromotionStep(PromotionKind Kind, Item Item, DateTimeOffset? StartUtc = null, DateTimeOffset? EndUtc = null);

/// <summary>
/// Works out what each plan's Outlook event needs. Pure: the agent carries the steps out.
///
/// Which side changed is told by comparing the plan and the event with the time rNotice last
/// wrote (<see cref="Item.CalendarStartUtc"/>): the plan differs → you re-planned here, move the
/// event; the event differs → you moved it in Outlook, follow it. rNotice never touches an event
/// it didn't create — every step is about an event whose id it stored itself.
/// </summary>
public static class Promotion
{
    public static IReadOnlyList<PromotionStep> Plan(
        IEnumerable<Item> items, IReadOnlyDictionary<string, PromotedEvent> events,
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, int defaultMinutes)
    {
        var steps = new List<PromotionStep>();
        foreach (var item in items)
        {
            if (item.Source == Source.Calendar) continue;   // meetings are already on the calendar

            var wanted = item.PromoteToCalendar && item.Stage == Stage.Planned && item.PlannedStartUtc is not null;
            var has = item.CalendarEventId is not null;

            if (!wanted)
            {
                if (has) steps.Add(new(PromotionKind.Remove, item));
                continue;
            }

            var start = item.PlannedStartUtc!.Value;
            var end = start.AddMinutes(item.PlannedMinutes ?? defaultMinutes);

            if (!has)
            {
                steps.Add(new(PromotionKind.Create, item, start, end));
                continue;
            }

            if (!events.TryGetValue(item.CalendarEventId!, out var ev))
            {
                // Missing from a window that covers it: deleted in Outlook. Outside the window
                // there's no telling, so nothing happens.
                var written = item.CalendarStartUtc ?? start;
                if (written >= windowStartUtc && written < windowEndUtc)
                    steps.Add(new(PromotionKind.Lost, item));
                continue;
            }

            if (start != item.CalendarStartUtc || end != item.CalendarEndUtc)
                steps.Add(new(PromotionKind.Move, item, start, end));
            else if (ev.StartUtc != item.CalendarStartUtc || ev.EndUtc != item.CalendarEndUtc)
                steps.Add(new(PromotionKind.FollowOutlook, item, ev.StartUtc, ev.EndUtc));
        }
        return steps;
    }
}
