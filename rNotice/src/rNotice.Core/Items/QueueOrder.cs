namespace rNotice.Core.Items;

/// <summary>
/// How the queue is ordered and what counts as urgent.
///
/// Priority alone isn't enough. Every meeting is P1, so fourteen days of them would sit above
/// an overdue P2 task. Found on the first real run. So: items whose fire time has come first,
/// by priority then time; upcoming items after, soonest first.
/// </summary>
public static class QueueOrder
{
    /// <summary>Its moment has come — fired, or due to — and it hasn't been cleared.</summary>
    public static bool IsCurrent(Item item, DateTimeOffset nowUtc) =>
        item.FireAtUtc is not { } fire || fire <= nowUtc;

    /// <summary>What the tray counts as urgent: current, P1 or P2, still in Intake.</summary>
    public static bool NeedsAttention(Item item, DateTimeOffset nowUtc) =>
        item.Stage == Stage.Intake && item.Classification.Priority <= Priority.P2 && IsCurrent(item, nowUtc)
        && !(item.SnoozedUntilUtc > nowUtc);

    public static IEnumerable<Item> Sort(IEnumerable<Item> items, DateTimeOffset nowUtc) =>
        items.OrderBy(i => IsCurrent(i, nowUtc) ? 0 : 1)
             .ThenBy(i => IsCurrent(i, nowUtc) ? (int)i.Classification.Priority : 0)
             .ThenBy(i => i.PlannedStartUtc ?? i.DueUtc ?? i.FireAtUtc ?? i.ReceivedUtc);

    /// <summary>
    /// The local calendar day an item belongs to: when you planned to do it, else when it's due,
    /// else when it arrived. Local, not UTC — a 17:00 meeting in Mountain time is 23:00 UTC, and grouping by
    /// UTC would put evening meetings on the wrong day.
    /// </summary>
    public static DateOnly DayOf(Item item, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.PlannedStartUtc ?? item.DueUtc ?? item.ReceivedUtc, zone).DateTime);

    /// <summary>
    /// Grouped by day, days in order, and within each day the usual queue order. The day sort is
    /// stable, so "needs you now" still leads inside today.
    /// </summary>
    public static IEnumerable<Item> SortByDay(IEnumerable<Item> items, DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        Sort(items, nowUtc).OrderBy(i => DayOf(i, zone));
}
