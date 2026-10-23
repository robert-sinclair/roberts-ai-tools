using System.Text.Json;
using rNotice.Core.Sources;

namespace rNotice.Core.Graph;

/// <summary>
/// One round of inbox changes: new or changed messages, ids that left the inbox, and the
/// <c>deltaLink</c> to start the next round from.
/// </summary>
public sealed record MailDelta(
    IReadOnlyList<MailMessage> Changed,
    IReadOnlyList<string> RemovedIds,
    string DeltaLink,
    bool Restarted,
    IReadOnlyList<(string Id, bool IsRead)>? ReadChanges = null);

/// <summary>
/// Reads the inbox through <c>messages/delta</c> and Sent Items for the reply rule. Read-only:
/// nothing here sends, moves, flags or marks anything as read.
/// (<see href="https://learn.microsoft.com/en-us/graph/api/message-delta">message: delta</see>)
/// </summary>
public sealed class MailClient(GraphClient graph)
{
    private const string Select = "subject,from,toRecipients,receivedDateTime,conversationId,bodyPreview,webLink,isRead";

    private readonly GraphClient _graph = graph;

    /// <summary>
    /// Changes since <paramref name="deltaLink"/>, or — with no link, or one Graph no longer
    /// accepts — a fresh round starting at <paramref name="backfillFromUtc"/>. The filter goes on
    /// the first request only; Graph carries it inside the links after that.
    ///
    /// Follows <c>nextLink</c> to the end. Stopping early would lose changes for good, since the
    /// <c>deltaLink</c> only comes on the last page.
    /// </summary>
    public async Task<MailDelta> GetInboxChangesAsync(string? deltaLink, DateTimeOffset backfillFromUtc, CancellationToken ct)
    {
        if (deltaLink is not null)
        {
            try
            {
                return await RunRoundAsync(deltaLink, restarted: false, ct).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.Status is 410 || ex.Code is "SyncStateNotFound" or "syncStateNotFound" or "resyncRequired")
            {
                // The token expired. Start again from the backfill window; ingest de-duplicates.
            }
        }

        var since = Uri.EscapeDataString(backfillFromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var initial = $"/me/mailFolders/inbox/messages/delta?$select={Select}&$filter=receivedDateTime%20ge%20{since}";
        return await RunRoundAsync(initial, restarted: true, ct).ConfigureAwait(false);
    }

    private async Task<MailDelta> RunRoundAsync(string firstUrl, bool restarted, CancellationToken ct)
    {
        var changed = new List<MailMessage>();
        var removed = new List<string>();
        var reads = new List<(string, bool)>();
        string? url = firstUrl;
        string? deltaLink = null;

        while (url is not null)
        {
            var page = await _graph.GetAsync(url, ct).ConfigureAwait(false);
            foreach (var entry in page.GetProperty("value").EnumerateArray())
            {
                if (entry.TryGetProperty("@removed", out _))
                    removed.Add(entry.GetProperty("id").GetString()!);
                else if (Map(entry) is { } message)
                    changed.Add(message);
                else if (entry.TryGetProperty("isRead", out var isRead) && isRead.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    reads.Add((entry.GetProperty("id").GetString()!, isRead.GetBoolean()));
            }

            url = page.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (page.TryGetProperty("@odata.deltaLink", out var delta)) deltaLink = delta.GetString();
        }

        return new MailDelta(changed, removed, deltaLink
            ?? throw new GraphException(0, "noDeltaLink", "Delta round ended without a deltaLink"), restarted, reads);
    }

    /// <summary>
    /// conversationId → when you first sent in it, from Sent Items over the lookback window.
    /// Fetched fresh each poll, before the inbox, so a reply arriving right after you sent is
    /// matched.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetFirstSentByConversationAsync(
        DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var since = Uri.EscapeDataString(sinceUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var path = $"/me/mailFolders/sentitems/messages?$filter=sentDateTime%20ge%20{since}" +
                   "&$select=conversationId,sentDateTime&$top=100";

        var first = new Dictionary<string, DateTimeOffset>();
        await foreach (var m in _graph.GetPagedAsync(path, ct).ConfigureAwait(false))
        {
            if (Str(m, "conversationId") is not { } conv || Str(m, "sentDateTime") is not { } sentText) continue;
            var sent = DateTimeOffset.Parse(sentText, System.Globalization.CultureInfo.InvariantCulture);
            if (!first.TryGetValue(conv, out var known) || sent < known) first[conv] = sent;
        }
        return first;
    }

    /// <summary>
    /// One message's full body, for picking a link out of it. The caller keeps what it needs —
    /// a Salesforce record ID — and drops the body: rNotice never stores a full message.
    /// </summary>
    public async Task<string> GetBodyAsync(string messageId, CancellationToken ct)
    {
        var m = await _graph.GetAsync($"/me/messages/{Uri.EscapeDataString(messageId)}?$select=body", ct).ConfigureAwait(false);
        return m.TryGetProperty("body", out var body) && body.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
    }

    /// <summary>
    /// A full message, or null for an entry that only says something changed — delta also
    /// reports read/unread flips, which may not carry the selected fields. Those are picked up
    /// separately as read changes.
    /// </summary>
    internal static MailMessage? Map(JsonElement m)
    {
        if (Str(m, "receivedDateTime") is not { } received ||
            !m.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object)
            return null;

        var sender = from.GetProperty("emailAddress");
        return new MailMessage(
            Id: m.GetProperty("id").GetString()!,
            ConversationId: Str(m, "conversationId") ?? "",
            Subject: Str(m, "subject") ?? "(no subject)",
            FromAddress: Str(sender, "address") ?? "",
            To: m.TryGetProperty("toRecipients", out var to) && to.ValueKind == JsonValueKind.Array
                ? to.EnumerateArray().Select(r => Str(r.GetProperty("emailAddress"), "address") ?? "").ToList()
                : [],
            ReceivedUtc: DateTimeOffset.Parse(received, System.Globalization.CultureInfo.InvariantCulture),
            FromName: Str(sender, "name"),
            BodyPreview: Str(m, "bodyPreview"),
            WebLink: Str(m, "webLink"),
            // Invites, responses and cancellations are eventMessage subtypes. The reply spike
            // found every meeting false alarm here.
            IsMeetingMessage: Str(m, "@odata.type")?.Contains("eventMessage", StringComparison.OrdinalIgnoreCase) == true,
            IsRead: m.TryGetProperty("isRead", out var read) && read.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? read.GetBoolean() : null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
