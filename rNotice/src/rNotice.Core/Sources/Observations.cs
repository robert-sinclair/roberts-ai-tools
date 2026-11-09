namespace rNotice.Core.Sources;

// What a poller hands to intake. Deliberately not Graph DTOs: the pollers map Graph's JSON onto
// these, so the rules can be tested without a network or a Graph SDK in sight.

/// <summary>Graph's <c>freeBusyStatus</c> values.</summary>
public enum ShowAs
{
    Unknown,
    Free,
    Tentative,
    Busy,
    Oof,
    WorkingElsewhere
}

public enum ChatType
{
    OneOnOne,
    Group,
    Meeting
}

/// <summary>One instance from <c>calendarView</c>. Recurrences arrive already expanded.</summary>
public sealed record CalendarEvent(
    string Id,
    string Subject,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool IsAllDay,
    bool IsCancelled,
    ShowAs ShowAs,
    IReadOnlyList<string> Categories,
    string? Organizer = null,
    // You organised it. Your own name isn't shown as who it's from.
    bool IsOrganizer = false,
    string? JoinUrl = null,
    string? WebLink = null,
    // The description, as text, without the Teams join block.
    string? Body = null,
    // Who's invited, as Graph lists them (the organiser isn't in it). Read 2026-11-06 for the
    // "not joined" takeover; Robert wants them searchable later.
    IReadOnlyList<EventAttendee>? Attendees = null,
    // Part of a series (Graph type "occurrence" or "exception"). rNotice never creates one.
    bool IsRecurring = false)
{
    /// <summary>
    /// One of rNotice's own planned-task events: it carries rNotice's category and isn't part
    /// of a series. A tagged series is a task you made recurring in Outlook (2026-11-09): from
    /// then on it's a meeting like any other, since no rNotice item owns its occurrences.
    /// </summary>
    public bool IsRNoticeTask(string category) =>
        !IsRecurring && Categories.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Someone besides you is in it: someone else organised it, or you invited anyone other than
    /// yourself.
    /// </summary>
    public bool HasOthers(string myAddress) =>
        !IsOrganizer || (Attendees ?? []).Any(a => !string.Equals(a.Address, myAddress, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One invitee: <c>attendee.emailAddress</c> and <c>type</c> (required, optional, resource).</summary>
public sealed record EventAttendee(string? Name, string? Address, string? Type);

/// <summary>One inbox message from <c>messages/delta</c>.</summary>
public sealed record MailMessage(
    string Id,
    string ConversationId,
    string Subject,
    string FromAddress,
    IReadOnlyList<string> To,
    DateTimeOffset ReceivedUtc,
    string? FromName = null,
    string? BodyPreview = null,
    string? WebLink = null,
    // Graph types invites, responses and cancellations as eventMessage*. The poller sets this
    // from @odata.type.
    bool IsMeetingMessage = false,
    // Read in Outlook. Noted on the item, never acted on — reading isn't handling.
    bool? IsRead = null);

/// <summary>One message from a watched chat, read since the chat's watermark.</summary>
public sealed record ChatMessage(
    string Id,
    string ChatId,
    ChatType ChatType,
    string? FromUserId,
    IReadOnlyList<string> MentionedUserIds,
    DateTimeOffset CreatedUtc,
    bool IsSystemMessage = false,
    string? FromName = null,
    string? TextPreview = null,
    string? WebUrl = null,
    // The chat's topic, or its other members for a chat with none.
    string? ChatName = null);

/// <summary>
/// One row of the <c>Actions</c> table, every cell as Graph returned it. Cells are objects
/// because Graph returns numbers for numeric cells — which is exactly how an ISO date that Excel
/// silently converted to a serial number shows up.
/// </summary>
public sealed record WorkbookRow(
    object? Id,
    object? Title,
    object? Details,
    object? Client,
    object? DueUtc,
    object? LeadMinutes,
    object? Priority,
    object? Interrupt,
    object? Status,
    string? WebUrl = null);
