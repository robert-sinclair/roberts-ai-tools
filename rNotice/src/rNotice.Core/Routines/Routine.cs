using System.Text.Json;
using System.Text.RegularExpressions;
using rNotice.Core.Items;
using rNotice.Core.Rules;

namespace rNotice.Core.Routines;

/// <summary>Where a Teams chat's item is with the classify routine (ROUTINE-CLASSIFY.md).</summary>
public enum RoutineStatus
{
    /// <summary>New messages; sent once the chat has been quiet for a minute.</summary>
    Waiting,

    /// <summary>A request is out. <see cref="RoutineInfo.RequestId"/> names it.</summary>
    Sent,

    /// <summary>The routine's result was applied.</summary>
    Answered,

    /// <summary>No result within the timeout. Sent again with the chat's next message.</summary>
    NoAnswer,

    /// <summary>The routine couldn't be called: not set up, or the call failed.</summary>
    Unavailable
}

/// <summary>An item's routine state. Stored encrypted, as one JSON value.</summary>
public sealed record RoutineInfo
{
    public required RoutineStatus Status { get; init; }

    /// <summary>The request in flight, or the one whose result was applied. The only link between a call and its result file.</summary>
    public Guid? RequestId { get; init; }

    public DateTimeOffset? SentUtc { get; init; }

    /// <summary>The routine's answer as given: <c>P1</c>, <c>P2</c>, <c>P3</c> or <c>N0</c>.</summary>
    public string? Priority { get; init; }

    /// <summary>0–100: how sure the routine was of <see cref="Priority"/>.</summary>
    public int? Confidence { get; init; }

    /// <summary>The routine's description of the task.</summary>
    public string? Description { get; init; }

    /// <summary>Under this, the routine's priority is shown as a suggestion and not acted on.</summary>
    public const int SuggestBelow = 60;

    /// <summary>An N0 this sure or more closes the item.</summary>
    public const int CloseNotATaskAt = 95;

    /// <summary>Applied, but too unsure to act on: shown as "Routine suggests …".</summary>
    public bool IsSuggestion => Status == RoutineStatus.Answered && Confidence < SuggestBelow;
}

/// <summary>One Teams message as sent to the routine.</summary>
public sealed record RoutineMessage(string From, DateTimeOffset SentUtc, string Text);

/// <summary>What the routine wrote to <c>&lt;requestId&gt;.json</c>, checked.</summary>
public sealed record RoutineResult(
    Guid RequestId,
    string Priority,
    int Confidence,
    string Title,
    string Summary,
    string Description,
    string? CaseNumber,
    string? CaseUrl);

/// <summary>What the routine found for a case lookup: record IDs as 18 characters.</summary>
public sealed record CaseLookupResult(Guid RequestId, IReadOnlyList<(string CaseNumber, string RecordId)> Found);

/// <summary>
/// The pure half of the classify routine: what to send, when, and what a result does to an
/// item. The calls themselves are in the agent. See ROUTINE-CLASSIFY.md.
/// </summary>
public static class Routine
{
    /// <summary>How long a chat must be quiet before it's sent: an ongoing chat isn't judged half-way.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    /// <summary>A request with no result after this is given up on.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    /// <summary>Messages sent per request, newest kept.</summary>
    public const int MaxMessages = 20;

    /// <summary>Characters per message, as IT was told (IT-REQUEST-ANTHROPIC-API.md).</summary>
    public const int MaxMessageChars = 2000;

    /// <summary>How far back a request's messages reach.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    /// <summary>
    /// The routine allows 30 fires an hour. rNotice stays under that, so a burst of chats waits
    /// rather than failing.
    /// </summary>
    public const int MaxFiresPerHour = 25;

    /// <summary>The only case link a result may carry. Anything else is dropped.</summary>
    private static readonly Regex LightningCase = new(
        @"^https://bravurasecurity\.lightning\.force\.com/lightning/r/Case/500[0-9A-Za-z]{15}/view$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ResultFile = new(
        @"^(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The request ID a file name carries, or null for a file that isn't a result.</summary>
    public static Guid? RequestIdOf(string fileName) =>
        ResultFile.Match(fileName) is { Success: true } m ? Guid.Parse(m.Groups["id"].Value) : null;

    /// <summary>
    /// Your own message saying you'll follow up. Matched anywhere in the message, ignoring case
    /// and curly apostrophes.
    /// </summary>
    public static bool IsFollowUp(string? text, IReadOnlyList<string> phrases)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var plain = text.Replace('’', '\'');
        return phrases.Any(p => p.Length > 0 && plain.Contains(p.Replace('’', '\''), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ready to send: waiting, and the chat has had no message for <see cref="Quiet"/>.</summary>
    public static bool IsDue(Item item, DateTimeOffset? lastMessageUtc, DateTimeOffset nowUtc) =>
        item is { Stage: Stage.Intake, Routine.Status: RoutineStatus.Waiting } &&
        nowUtc - (lastMessageUtc > item.ReceivedUtc ? lastMessageUtc.Value : item.ReceivedUtc) >= Quiet;

    /// <summary>The fire <c>text</c>: one JSON request, each message cut to 2000 characters, the newest 20.</summary>
    public static string RequestText(Guid requestId, string resultsFolder, string chatName, string? chatLink,
        IReadOnlyList<RoutineMessage> messages)
    {
        var recent = messages.OrderBy(m => m.SentUtc).TakeLast(MaxMessages).Select(m => new
        {
            from = m.From,
            sentUtc = m.SentUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            text = TextMatch.Truncate(m.Text, MaxMessageChars) ?? ""
        });

        return JsonSerializer.Serialize(new
        {
            requestId = requestId.ToString("D"),
            resultsFolder,
            chat = new { name = chatName, type = "oneOnOne", link = chatLink },
            preferences = Array.Empty<string>(),
            messages = recent
        });
    }

    /// <summary>
    /// A result file's content, checked field by field. Null, with the reason, if any required
    /// field is missing or out of range. A case link that isn't exactly a Lightning case link
    /// is dropped, not trusted: the result came back from a model.
    /// </summary>
    public static RoutineResult? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Fail("not a JSON object", out error);

            if (Str(r, "requestId") is not { } idText || !Guid.TryParse(idText, out var id)) return Fail("no requestId", out error);
            var priority = Str(r, "priority");
            if (priority is not ("P1" or "P2" or "P3" or "N0")) return Fail("priority isn't P1, P2, P3 or N0", out error);
            if (!r.TryGetProperty("confidence", out var c) || c.ValueKind != JsonValueKind.Number ||
                !c.TryGetInt32(out var confidence) || confidence is < 0 or > 100)
                return Fail("confidence isn't 0 to 100", out error);
            if (Str(r, "title") is not { Length: > 0 } title) return Fail("no title", out error);

            var caseUrl = Str(r, "caseUrl") is { } url && LightningCase.IsMatch(url) ? url : null;
            return new RoutineResult(id, priority!, confidence,
                TextMatch.Truncate(title.Replace('\n', ' '), 80)!,
                TextMatch.Truncate(Str(r, "summary"), 300) ?? title,
                TextMatch.Truncate(Str(r, "description"), 2000) ?? "",
                CaseLinks.FindAnyCaseNumber(Str(r, "caseNumber")),
                caseUrl);
        }
        catch (JsonException ex)
        {
            return Fail($"not valid JSON ({ex.Message})", out error);
        }

        static RoutineResult? Fail(string why, out string? error)
        {
            error = why;
            return null;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s &&
            !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    }

    // ── Case lookups ────────────────────────────────────────────────────────────────────

    /// <summary>Case numbers per lookup request.</summary>
    public const int MaxCaseNumbers = 20;

    /// <summary>A case number the routine didn't find isn't asked about again for this long.</summary>
    public static readonly TimeSpan CaseRetry = TimeSpan.FromDays(1);

    /// <summary>A case not referenced for this long drops off the map.</summary>
    public static readonly TimeSpan CaseMapKeep = TimeSpan.FromDays(182);

    private static readonly Regex RecordId = new(@"^500[0-9A-Za-z]{12}(?:[0-9A-Za-z]{3})?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The case an item is about: a case number in the title ("Case 00038028", or a bare
    /// 000xxxxx such as a meeting called "00038033"), else one with "Case" in front in its text.
    /// </summary>
    public static string? CaseNumberOf(Item item) =>
        CaseLinks.FindAnyCaseNumber(item.Title) ?? CaseLinks.FindCaseNumber(item.Snippet);

    /// <summary>
    /// An open item about a case with no link yet. Mail first has its own message read for a
    /// link (<c>CaseUrl</c> null means not looked yet, empty means looked and none), so mail
    /// qualifies only once that found nothing. Groups carry their members' links already.
    /// </summary>
    public static bool NeedsCaseLink(Item item) =>
        item.IsOpen && item.Source != Source.Group && CaseNumberOf(item) is not null &&
        (item.Source == Source.Mail ? item.CaseUrl == "" : string.IsNullOrEmpty(item.CaseUrl));

    /// <summary>The fire <c>text</c> for a case lookup.</summary>
    public static string CaseLookupText(Guid requestId, string resultsFolder, IReadOnlyList<string> caseNumbers) =>
        JsonSerializer.Serialize(new
        {
            requestId = requestId.ToString("D"),
            kind = "caseLookup",
            resultsFolder,
            caseNumbers = caseNumbers.Take(MaxCaseNumbers)
        });

    /// <summary>
    /// A case lookup's result file, checked. Each found case needs an eight-digit case number
    /// and a Case record ID (prefix 500, 15 or 18 characters); anything else is skipped.
    /// </summary>
    public static CaseLookupResult? ParseCaseLookup(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("requestId", out var idProp) ||
                idProp.ValueKind != JsonValueKind.String || !Guid.TryParse(idProp.GetString(), out var id))
            {
                error = "no requestId";
                return null;
            }

            var found = new List<(string, string)>();
            if (r.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Array)
                foreach (var c in cases.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var number = c.TryGetProperty("caseNumber", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var record = c.TryGetProperty("caseId", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()?.Trim() : null;
                    if (number is null || CaseLinks.FindAnyCaseNumber(number) != number.Trim() || record is null || !RecordId.IsMatch(record))
                        continue;
                    found.Add((number.Trim(), CaseLinks.To18(record)));
                }
            return new CaseLookupResult(id, found);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON ({ex.Message})";
            return null;
        }
    }

    /// <summary>A request went out.</summary>
    public static Item Sent(Item item, Guid requestId, DateTimeOffset nowUtc) =>
        item with { Routine = new RoutineInfo { Status = RoutineStatus.Sent, RequestId = requestId, SentUtc = nowUtc } };

    /// <summary>A request out longer than <see cref="Timeout"/> is given up on.</summary>
    public static bool IsOverdue(Item item, DateTimeOffset nowUtc) =>
        item.Routine is { Status: RoutineStatus.Sent, SentUtc: { } sent } && nowUtc - sent > Timeout;

    /// <summary>
    /// Applies a result (ROUTINE-CLASSIFY.md, "What rNotice does with a result"):
    /// <list type="bullet">
    /// <item>Title (unless you edited it), description, case link and confidence always.</item>
    /// <item>Under 60: the priority is only a suggestion; the item keeps its classification.</item>
    /// <item>N0 at 95 or more: closed as Done — not Dismissed, which would hide the chat's
    /// later messages for good once it's purged.</item>
    /// <item>Otherwise P1 alerts, P2 toasts, P3 and N0 are silent.</item>
    /// </list>
    /// The caller has already checked the result belongs to this item's request.
    /// </summary>
    public static Item Apply(Item item, RoutineResult result, DateTimeOffset nowUtc)
    {
        var applied = item with
        {
            Title = item.TitleEdited ? item.Title : result.Title,
            CaseUrl = result.CaseUrl ?? item.CaseUrl,
            Routine = new RoutineInfo
            {
                Status = RoutineStatus.Answered,
                RequestId = result.RequestId,
                SentUtc = item.Routine?.SentUtc,
                Priority = result.Priority,
                Confidence = result.Confidence,
                Description = result.Description
            }
        };

        if (result.Confidence < RoutineInfo.SuggestBelow) return applied;

        if (result.Priority == "N0" && result.Confidence >= RoutineInfo.CloseNotATaskAt)
            return applied with
            {
                Stage = Stage.Done,
                Note = $"Routine: not a task ({result.Confidence}%)",
                NoteLink = null,
                Classification = new Classification(Priority.P4, Interrupt.Silent, ClassifiedBy.Model, result.Summary)
            };

        var (priority, interrupt) = result.Priority switch
        {
            "P1" => (Priority.P1, Interrupt.Alert),
            "P2" => (Priority.P2, Interrupt.Toast),
            "P3" => (Priority.P3, Interrupt.Silent),
            _ => (Priority.P4, Interrupt.Silent)
        };
        return applied with
        {
            Classification = new Classification(priority, interrupt, ClassifiedBy.Model, result.Summary),
            // Reach you now, at the new level.
            FireAtUtc = nowUtc,
            LastFiredUtc = null
        };
    }
}
