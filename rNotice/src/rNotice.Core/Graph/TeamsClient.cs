using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using rNotice.Core.Sources;

namespace rNotice.Core.Graph;

/// <summary>A chat you're in, for the picker and for deciding which chats to read.</summary>
public sealed record ChatInfo(string Id, ChatType Type, string Name, DateTimeOffset? LastMessageUtc);

/// <summary>
/// Reads your Teams chats with <c>Chat.Read</c>. Read-only: there is no send scope.
///
/// There is no per-chat delta for delegated access — the only chat delta,
/// <c>/users/{id}/chats/getAllMessages/delta</c>, is application-only
/// (<see href="https://learn.microsoft.com/en-us/graph/api/chatmessage-delta">chatMessage: delta</see>).
/// So each chat is read with a watermark: messages whose <c>lastModifiedDateTime</c> is after
/// the last one seen
/// (<see href="https://learn.microsoft.com/en-us/graph/api/chat-list-messages">list messages in a chat</see>).
/// </summary>
public sealed class TeamsClient(GraphClient graph)
{
    private readonly GraphClient _graph = graph;

    /// <summary>
    /// Every chat you're in, with a display name and when its last message arrived. A chat with
    /// no topic is named after the other members, the way Teams does it.
    /// </summary>
    public async Task<IReadOnlyList<ChatInfo>> ListChatsAsync(string myUserId, CancellationToken ct)
    {
        var chats = new List<ChatInfo>();
        await foreach (var c in _graph.GetPagedAsync("/me/chats?$expand=members,lastMessagePreview&$top=50", ct).ConfigureAwait(false))
            if (MapChat(c, myUserId) is { } chat) chats.Add(chat);
        return chats;
    }

    internal static ChatInfo? MapChat(JsonElement c, string myUserId)
    {
        var type = Str(c, "chatType") switch
        {
            "oneOnOne" => ChatType.OneOnOne,
            "group" => ChatType.Group,
            "meeting" => ChatType.Meeting,
            _ => (ChatType?)null
        };
        if (type is null) return null;

        var name = Str(c, "topic");
        if (string.IsNullOrWhiteSpace(name))
        {
            var others = c.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array
                ? members.EnumerateArray()
                    .Where(m => Str(m, "userId") != myUserId)
                    .Select(m => Str(m, "displayName"))
                    .OfType<string>()
                    .ToList()
                : [];
            name = others.Count == 0 ? "(unnamed chat)" : string.Join(", ", others);
        }

        DateTimeOffset? last = null;
        if (c.TryGetProperty("lastMessagePreview", out var preview) && preview.ValueKind == JsonValueKind.Object &&
            Str(preview, "createdDateTime") is { } created)
            last = DateTimeOffset.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        return new ChatInfo(c.GetProperty("id").GetString()!, type.Value, name!, last);
    }

    /// <summary>
    /// Messages in one chat created or changed after <paramref name="sinceUtc"/>, oldest first,
    /// and the newest <c>lastModifiedDateTime</c> seen — the next watermark. Follows paging to
    /// the end; with nothing new, the watermark stays where it was.
    /// </summary>
    public async Task<(IReadOnlyList<ChatMessage> Messages, DateTimeOffset Watermark)> GetMessagesSinceAsync(
        ChatInfo chat, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var since = sinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var path = $"/me/chats/{Uri.EscapeDataString(chat.Id)}/messages?$top=50" +
                   $"&$orderby=lastModifiedDateTime%20desc&$filter=lastModifiedDateTime%20gt%20{since}";

        var messages = new List<ChatMessage>();
        var watermark = sinceUtc;
        await foreach (var m in _graph.GetPagedAsync(path, ct).ConfigureAwait(false))
        {
            if (Str(m, "lastModifiedDateTime") is { } modified &&
                DateTimeOffset.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) is var at && at > watermark)
                watermark = at;
            messages.Add(MapMessage(m, chat));
        }
        messages.Sort((a, b) => a.CreatedUtc.CompareTo(b.CreatedUtc));
        return (messages, watermark);
    }

    internal static ChatMessage MapMessage(JsonElement m, ChatInfo chat)
    {
        JsonElement user = default;
        var hasUser = m.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object &&
                      from.TryGetProperty("user", out user) && user.ValueKind == JsonValueKind.Object;

        var mentioned = m.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array
            ? mentions.EnumerateArray()
                .Select(x => x.TryGetProperty("mentioned", out var who) && who.ValueKind == JsonValueKind.Object &&
                             who.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "id") : null)
                .OfType<string>()
                .ToList()
            : [];

        var html = m.TryGetProperty("body", out var body) ? Str(body, "content") : null;
        var text = PlainText(html);
        var hasAttachments = m.TryGetProperty("attachments", out var att) && att.ValueKind == JsonValueKind.Array && att.GetArrayLength() > 0;

        return new ChatMessage(
            Id: m.GetProperty("id").GetString()!,
            ChatId: chat.Id,
            ChatType: chat.Type,
            FromUserId: hasUser ? Str(user, "id") : null,
            MentionedUserIds: mentioned,
            CreatedUtc: DateTimeOffset.Parse(Str(m, "createdDateTime")!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            // Deleted messages and system events (member added, chat renamed) aren't messages to you.
            IsSystemMessage: Str(m, "messageType") != "message" || Str(m, "deletedDateTime") is not null,
            FromName: hasUser ? Str(user, "displayName") : null,
            TextPreview: string.IsNullOrWhiteSpace(text) ? (hasAttachments ? "(attachment)" : null) : text,
            WebUrl: Str(m, "webUrl"),
            ChatName: chat.Name);
    }

    private static readonly Regex Breaks = new(@"<\s*(br|/p|/div|/li)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Blank = new(@"[ \t]*\n[\s]*\n+", RegexOptions.Compiled);

    /// <summary>Chat bodies are HTML. Line breaks kept, tags dropped, entities decoded.</summary>
    internal static string? PlainText(string? html)
    {
        if (html is null) return null;
        var text = Breaks.Replace(html, "\n");
        text = Tags.Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = Blank.Replace(text.Replace("\r", ""), "\n");
        return text.Trim();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
