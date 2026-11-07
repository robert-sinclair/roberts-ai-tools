using rNotice.Core.Items;

namespace rNotice.Core.Rules;

/// <summary>
/// Joined-meeting detection from your own Teams presence (build step 11).
///
/// Only the call states count. Teams sets "In a meeting" from your Outlook calendar, so it shows
/// for every scheduled meeting whether you joined or not; "In a call" and "Presenting" come from
/// the Teams app itself
/// (<see href="https://learn.microsoft.com/en-us/microsoftteams/presence-admins">User presence in Teams</see>).
/// </summary>
public static class Presence
{
    private static readonly HashSet<string> CallActivities =
        new(["InACall", "InAConferenceCall", "Presenting"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Teams shows you on a call or presenting.</summary>
    public static bool IsInACall(string? activity) => activity is not null && CallActivities.Contains(activity);

    /// <summary>Meetings under way, joined or not: started, not yet ended, not done or dismissed.</summary>
    public static IReadOnlyList<Item> UnderWay(IEnumerable<Item> items, DateTimeOffset nowUtc) =>
        items.Where(i => i.Source == Source.Calendar && i.Stage == Stage.Planned && i.DueUtc <= nowUtc).ToList();

    /// <summary>
    /// Meetings under way that aren't marked joined: started, not yet ended (a meeting's item goes
    /// when it ends), not done or dismissed. A call marks one of these joined.
    /// </summary>
    public static IReadOnlyList<Item> UnderWayNotJoined(IEnumerable<Item> items, DateTimeOffset nowUtc) =>
        items.Where(i => i.Source == Source.Calendar && i.Stage == Stage.Planned &&
                         i.DueUtc <= nowUtc && i.JoinedUtc is null)
             .ToList();

    /// <summary>
    /// The meeting a call means you joined: the one under way. With two or more overlapping,
    /// presence can't say which, so none is marked.
    /// </summary>
    public static Item? MeetingJoined(IEnumerable<Item> items, DateTimeOffset nowUtc, string? activity) =>
        IsInACall(activity) && UnderWayNotJoined(items, nowUtc) is [var only] ? only : null;

    /// <summary>
    /// Asked before a meeting takes over the screen (Robert, 2026-11-07: if I'm already in it,
    /// don't overlay). A meeting under way waits for a presence reading no older than
    /// <paramref name="fresh"/>, but no more than <paramref name="maxWait"/> after it starts, so a
    /// failed read never swallows the takeover. In a call: not shown. A meeting that hasn't
    /// started is shown without asking: presence is read only while a meeting is under way, as
    /// promised to IT.
    /// </summary>
    public static TakeoverCheck BeforeTakeover(Item item, DateTimeOffset nowUtc, string? activity,
        DateTimeOffset? readUtc, TimeSpan fresh, TimeSpan maxWait)
    {
        if (item.Source != Source.Calendar || item.DueUtc > nowUtc) return TakeoverCheck.Show;
        if (readUtc is not { } read || nowUtc - read > fresh)
            return nowUtc - item.DueUtc < maxWait ? TakeoverCheck.Wait : TakeoverCheck.Show;
        return IsInACall(activity) ? TakeoverCheck.InACall : TakeoverCheck.Show;
    }
}

public enum TakeoverCheck
{
    /// <summary>Take over.</summary>
    Show,
    /// <summary>Hold for a presence reading; asked again next tick.</summary>
    Wait,
    /// <summary>Teams has you in a call: no takeover.</summary>
    InACall,
}
