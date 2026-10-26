using System.Globalization;
using System.Text.Json;
using rNotice.Core.Sources;

namespace rNotice.Core.Graph;

/// <summary>
/// Reads the calendar through <c>calendarView</c>, which expands recurrences server-side.
/// Read-only in this build.
/// </summary>
public sealed class CalendarClient(GraphClient graph)
{
    private const string Select =
        "subject,start,end,isAllDay,isCancelled,showAs,categories,organizer,isOrganizer,onlineMeeting,webLink";

    private readonly GraphClient _graph = graph;

    /// <summary>
    /// Every instance overlapping the window. <c>startDateTime</c> and <c>endDateTime</c> are
    /// required; paging is followed to the end, since a missed page is a missed takeover.
    /// </summary>
    public async Task<IReadOnlyList<CalendarEvent>> GetViewAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var path = "/me/calendarView" +
                   $"?startDateTime={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}" +
                   $"&endDateTime={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}" +
                   $"&$select={Select}&$top=50";

        var events = new List<CalendarEvent>();
        await foreach (var e in _graph.GetPagedAsync(path, ct).ConfigureAwait(false))
            events.Add(Map(e));
        return events;
    }

    // ── Writes: only ever events rNotice creates, tagged with its category ─────────────────

    private static object When(DateTimeOffset utc) =>
        new { dateTime = utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" };

    /// <summary>
    /// A time block for one planned task: yours alone (no attendees, ever), tentative so it never
    /// silences rNotice's own alerts, tagged so rNotice can recognise its own, with Outlook's
    /// reminder at the start as the backstop to rNotice's earlier alert.
    /// (<see href="https://learn.microsoft.com/en-us/graph/api/user-post-events">create event</see>)
    /// </summary>
    public async Task<string> CreateEventAsync(string subject, string body, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string category, int reminderMinutesBeforeStart, CancellationToken ct)
    {
        var created = await _graph.SendJsonAsync(HttpMethod.Post, "/me/events", new
        {
            subject,
            body = new { contentType = "text", content = body },
            start = When(startUtc),
            end = When(endUtc),
            categories = new[] { category },
            showAs = "tentative",
            isReminderOn = true,
            reminderMinutesBeforeStart,
            attendees = Array.Empty<object>()
        }, ct).ConfigureAwait(false);
        return created.GetProperty("id").GetString()!;
    }

    public Task RenameEventAsync(string eventId, string subject, CancellationToken ct) =>
        _graph.SendJsonAsync(HttpMethod.Patch, $"/me/events/{Uri.EscapeDataString(eventId)}", new { subject }, ct);

    public Task MoveEventAsync(string eventId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken ct) =>
        _graph.SendJsonAsync(HttpMethod.Patch, $"/me/events/{Uri.EscapeDataString(eventId)}",
            new { start = When(startUtc), end = When(endUtc) }, ct);

    /// <summary>
    /// Deletes an event rNotice created. Re-reads it first and refuses unless it still carries
    /// rNotice's category, so a stored id can never take out a meeting or anything you made.
    /// Returns false if it was already gone; throws if it isn't rNotice's.
    /// </summary>
    public async Task<bool> DeleteOwnEventAsync(string eventId, string category, CancellationToken ct)
    {
        JsonElement ev;
        try
        {
            ev = await _graph.GetAsync($"/me/events/{Uri.EscapeDataString(eventId)}?$select=categories", ct).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            return false;
        }

        var ours = ev.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                   cats.EnumerateArray().Any(c => string.Equals(c.GetString(), category, StringComparison.OrdinalIgnoreCase));
        if (!ours)
            throw new InvalidOperationException($"Refusing to delete event {eventId}: it doesn't carry the '{category}' category.");

        await _graph.SendAsync(HttpMethod.Delete, $"/me/events/{Uri.EscapeDataString(eventId)}", null, ct).ConfigureAwait(false);
        return true;
    }

    internal static CalendarEvent Map(JsonElement e) => new(
        Id: e.GetProperty("id").GetString()!,
        Subject: Str(e, "subject") ?? "(no subject)",
        StartUtc: Time(e.GetProperty("start")),
        EndUtc: Time(e.GetProperty("end")),
        IsAllDay: Bool(e, "isAllDay"),
        IsCancelled: Bool(e, "isCancelled"),
        ShowAs: ParseShowAs(Str(e, "showAs")),
        Categories: e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
            ? cats.EnumerateArray().Select(c => c.GetString() ?? "").ToList()
            : [],
        IsOrganizer: Bool(e, "isOrganizer"),
        Organizer: e.TryGetProperty("organizer", out var org) && org.ValueKind == JsonValueKind.Object
            ? org.GetProperty("emailAddress").TryGetProperty("name", out var n) ? n.GetString() : null
            : null,
        // onlineMeeting.joinUrl, never onlineMeetingUrl: the latter is null for Teams meetings.
        JoinUrl: e.TryGetProperty("onlineMeeting", out var om) && om.ValueKind == JsonValueKind.Object
            ? Str(om, "joinUrl")
            : null,
        WebLink: Str(e, "webLink"));

    /// <summary>
    /// <c>dateTimeTimeZone</c>. With <c>Prefer: outlook.timezone="UTC"</c> the zone is UTC;
    /// anything else is converted rather than trusted, since a wrong zone is a takeover at the
    /// wrong time.
    /// </summary>
    internal static DateTimeOffset Time(JsonElement dtz)
    {
        var local = DateTime.Parse(dtz.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces);
        var zone = Str(dtz, "timeZone") ?? "UTC";

        if (zone.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }

    private static ShowAs ParseShowAs(string? s) => s?.ToLowerInvariant() switch
    {
        "free" => ShowAs.Free,
        "tentative" => ShowAs.Tentative,
        "busy" => ShowAs.Busy,
        "oof" => ShowAs.Oof,
        "workingelsewhere" => ShowAs.WorkingElsewhere,
        _ => ShowAs.Unknown
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
