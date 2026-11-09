using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using rNotice.Core.Items;
using rNotice.Core.Rules;

namespace rNotice.Core.Routines;

/// <summary>One Jira ticket as the Jira routine reported it, checked.</summary>
public sealed record JiraTicket(
    string Key,
    string Summary,
    string? Status,
    string? Priority,
    string? Owner,
    DateTimeOffset CreatedUtc,
    string? Type);

/// <summary>The last list the routine sent: when, for which filter, and the tickets.</summary>
public sealed record JiraSnapshot(DateTimeOffset FetchedUtc, string Jql, IReadOnlyList<JiraTicket> Tickets);

/// <summary>A priority and its tickets, oldest first.</summary>
public sealed record JiraGroup(string Priority, IReadOnlyList<JiraTicket> Tickets);

/// <summary>
/// The pure half of the Jira tab (build step 12, ROUTINE-JIRA.md). rNotice never talks to Jira:
/// it fires the Jira routine with a JQL filter, the routine reads Jira with its Atlassian
/// connector and writes <c>&lt;requestId&gt;.json</c> to OneDrive, and rNotice reads that.
///
/// Read only, both ways. Planning a ticket makes an rNotice item and writes nothing to Jira, and
/// marking that item Done says nothing about the ticket (Robert, 2026-11-09).
/// </summary>
public static class Jira
{
    /// <summary>Tickets per request. The routine stops here.</summary>
    public const int MaxTickets = 200;

    /// <summary>A request with no result after this is given up on.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    /// <summary>Opening the tab asks again once the list is this old.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);

    private static readonly Regex Key = new(@"^[A-Z][A-Z0-9_]{0,19}-[0-9]{1,9}$", RegexOptions.CultureInvariant);

    /// <summary>A ticket key such as <c>SAASK-123</c>. Anything else in a result is dropped.</summary>
    public static bool IsKey(string? key) => key is not null && Key.IsMatch(key);

    /// <summary>The fire <c>text</c>: the filter, and where to write the answer.</summary>
    public static string RequestText(Guid requestId, string resultsFolder, string jql) =>
        JsonSerializer.Serialize(new
        {
            requestId = requestId.ToString("D"),
            kind = "jiraTickets",
            resultsFolder,
            jql,
            maxTickets = MaxTickets
        });

    /// <summary>
    /// A result file, checked. The request ID must be there. Each ticket needs a valid key, a
    /// summary and a created date; a ticket missing any of them is skipped and counted. Text is
    /// cut to length: the result came back from a model.
    /// </summary>
    public static (Guid RequestId, IReadOnlyList<JiraTicket> Tickets, int Skipped)? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "requestId") is not { } idText || !Guid.TryParse(idText, out var id))
            {
                error = "no requestId";
                return null;
            }
            if (!r.TryGetProperty("tickets", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                error = "no tickets list";
                return null;
            }

            var tickets = new List<JiraTicket>();
            var skipped = 0;
            foreach (var t in list.EnumerateArray())
            {
                if (tickets.Count >= MaxTickets) break;
                if (t.ValueKind != JsonValueKind.Object || Str(t, "key") is not { } key || !IsKey(key) ||
                    Str(t, "summary") is not { } summary ||
                    !DateTimeOffset.TryParse(Str(t, "created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
                {
                    skipped++;
                    continue;
                }
                if (tickets.Any(x => x.Key == key)) continue;
                tickets.Add(new JiraTicket(key, TextMatch.Truncate(summary.Replace('\n', ' '), 200)!,
                    Short(Str(t, "status")), Short(Str(t, "priority")), Short(Str(t, "assignee")),
                    created.ToUniversalTime(), Short(Str(t, "type"))));
            }
            return (id, tickets, skipped);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON ({ex.Message})";
            return null;
        }

        static string? Short(string? s) => TextMatch.Truncate(s, 60);
    }

    /// <summary>
    /// Grouped by priority, highest first, then oldest first within each. Jira doesn't say how a
    /// site ranks its priorities, so the usual names are ranked here; a name not listed comes
    /// after them, and no priority (or "None") comes last.
    /// </summary>
    public static IReadOnlyList<JiraGroup> Grouped(IEnumerable<JiraTicket> tickets) =>
        tickets.GroupBy(t => string.IsNullOrWhiteSpace(t.Priority) ? "None" : t.Priority!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new JiraGroup(g.Key, g.OrderBy(t => t.CreatedUtc).ThenBy(t => t.Key, StringComparer.Ordinal).ToList()))
            .ToList();

    private static readonly string[] Ranked =
        ["Blocker", "Highest", "Critical", "High", "Major", "Medium", "Normal", "Minor", "Low", "Lowest", "Trivial"];

    private static int Rank(string priority) =>
        priority.Equals("None", StringComparison.OrdinalIgnoreCase) ? int.MaxValue
        : Array.FindIndex(Ranked, r => r.Equals(priority, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i
        : Ranked.Length;

    /// <summary>The ticket's page. Built here from the site setting, never taken from a result.</summary>
    public static string TicketUrl(string site, string key) => $"{site.TrimEnd('/')}/browse/{key}";

    /// <summary>
    /// The open item already made from this ticket, if any. A ticket planned and done before
    /// gets a new item; the done one stays as the record of that work.
    /// </summary>
    public static Item? OpenItemFor(IEnumerable<Item> items, string key) =>
        items.FirstOrDefault(i => i.Source == Source.Jira && i.IsOpen && KeyOf(i) == key);

    /// <summary>The ticket key an item was made from.</summary>
    public static string? KeyOf(Item item) =>
        item.Source == Source.Jira && item.SourceId.IndexOf('/') is var slash and > 0 ? item.SourceId[..slash] : null;

    /// <summary>
    /// A draft item for planning a ticket. Its own copy of what the ticket said today: the
    /// ticket can change in Jira and this doesn't follow, since nothing links them but the key.
    /// </summary>
    public static Item From(JiraTicket ticket, string site, DateTimeOffset nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        Source = Source.Jira,
        SourceId = $"{ticket.Key}/{Guid.NewGuid():N}",
        SourceRef = TicketUrl(site, ticket.Key),
        Title = $"{ticket.Key} {ticket.Summary}",
        TitleEdited = true,
        Snippet = string.Join("\n", new[]
        {
            ticket.Summary,
            string.Join(" · ", new[] { ticket.Type, ticket.Status, ticket.Owner is null ? null : $"Owner: {ticket.Owner}" }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            TicketUrl(site, ticket.Key)
        }.Where(s => !string.IsNullOrWhiteSpace(s))),
        From = ticket.Owner,
        ReceivedUtc = nowUtc,
        Stage = Stage.Intake,
        Classification = new Classification(Priority.P3, Interrupt.Silent, ClassifiedBy.User, "Jira ticket")
    };

    /// <summary>The snapshot as stored (encrypted, in the store's state table).</summary>
    public static string Serialize(JiraSnapshot snapshot) => JsonSerializer.Serialize(snapshot);

    public static JiraSnapshot? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<JiraSnapshot>(json); }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s &&
        !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
