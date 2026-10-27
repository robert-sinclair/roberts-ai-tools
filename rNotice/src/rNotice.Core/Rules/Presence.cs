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

    /// <summary>
    /// Meetings under way that aren't marked joined: started, not yet ended (a meeting's item goes
    /// when it ends), not done or dismissed. Presence is read only while this is non-empty.
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
}
