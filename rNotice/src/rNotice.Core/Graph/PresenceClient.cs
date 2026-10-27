using System.Text.Json;

namespace rNotice.Core.Graph;

/// <summary>
/// Your own Teams presence, <c>GET /me/presence</c> with <c>Presence.Read</c>
/// (<see href="https://learn.microsoft.com/en-us/graph/api/presence-get">get presence</see>).
/// Only <c>activity</c> is read; nothing else in the response is kept or passed on, as promised
/// to IT in IT-REQUEST-PRESENCE.md.
/// </summary>
public sealed class PresenceClient(GraphClient graph)
{
    private readonly GraphClient _graph = graph;

    public async Task<string?> GetActivityAsync(CancellationToken ct)
    {
        var presence = await _graph.GetAsync("/me/presence", ct).ConfigureAwait(false);
        return Activity(presence);
    }

    internal static string? Activity(JsonElement presence) =>
        presence.TryGetProperty("activity", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
}
