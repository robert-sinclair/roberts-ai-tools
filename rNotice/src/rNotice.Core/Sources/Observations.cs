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
    string? JoinUrl = null,
    string? WebLink = null);

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
    string? WebLink = null);

/// <summary>One message from a watched chat's <c>messages/delta</c>.</summary>
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
    string? WebUrl = null);

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
