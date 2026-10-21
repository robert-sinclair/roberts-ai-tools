using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Sources;

namespace rNotice.Tests;

/// <summary>Builders with sensible defaults, so each test states only what it's about.</summary>
internal static class Make
{
    public static readonly DateTimeOffset Now = new(2026, 10, 21, 14, 0, 0, TimeSpan.Zero);
    public static readonly Me User = new("user-me", "robert.sinclair@bravurasecurity.com");

    public static CalendarEvent Event(
        string subject = "Design review",
        DateTimeOffset? start = null,
        int minutes = 30,
        ShowAs showAs = ShowAs.Busy,
        bool allDay = false,
        bool cancelled = false,
        params string[] categories)
    {
        var s = start ?? Now.AddMinutes(10);
        return new CalendarEvent("evt-1", subject, s, s.AddMinutes(minutes), allDay, cancelled, showAs,
            categories, Organizer: "Alice", JoinUrl: "https://teams.microsoft.com/l/meetup-join/x");
    }

    public static MailMessage Mail(
        string from = "alice@contoso.com",
        string conversationId = "conv-1",
        string[]? to = null,
        string? preview = "Can you take a look?") =>
        new("msg-1", conversationId, "Quarterly certs", from,
            to ?? ["robert.sinclair@bravurasecurity.com", "bob@bravurasecurity.com"], Now,
            FromName: "Alice", BodyPreview: preview);

    public static ChatMessage Chat(
        string chatId = "chat-watched",
        ChatType type = ChatType.Group,
        string from = "user-alice",
        string[]? mentions = null,
        bool system = false,
        string? text = "Has anyone got the cert expiry list?") =>
        new("cm-1", chatId, type, from, mentions ?? [], Now, system, "Alice", text);

    public static WorkbookRow Row(
        string? id = "row-1",
        object? due = null,   // null here means the default below; no test needs a blank due
        object? lead = null,
        object? priority = null,
        object? interrupt = null,
        string? status = "Pending",
        object? client = null) =>
        new(id, "Rotate service account", "Contoso prod", client, due ?? "2026-10-21T16:00:00Z",
            lead, priority, interrupt, status);

    public static Item Item(
        Interrupt interrupt = Interrupt.Alert,
        Priority priority = Priority.P2,
        DateTimeOffset? fireAt = null,
        DateTimeOffset? due = null,
        Stage stage = Stage.Intake,
        string sourceId = "src-1") =>
        new()
        {
            Id = Guid.NewGuid(),
            Source = Source.Workbook,
            SourceId = sourceId,
            Title = "Something",
            ReceivedUtc = Now.AddHours(-1),
            Stage = stage,
            FireAtUtc = fireAt ?? Now.AddMinutes(-1),
            DueUtc = due,
            Classification = new Classification(priority, interrupt, ClassifiedBy.Rule, "test")
        };
}
