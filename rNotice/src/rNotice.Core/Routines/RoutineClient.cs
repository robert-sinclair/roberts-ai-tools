using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace rNotice.Core.Routines;

/// <summary>What starting a routine run returned.</summary>
public sealed record RoutineFire(string SessionId, string SessionUrl);

public sealed class RoutineException(string message) : Exception(message);

/// <summary>
/// Starts the classify routine through its API trigger. The call returns as soon as the
/// session exists: no result comes back here. The routine writes it to OneDrive.
/// (<see href="https://code.claude.com/docs/en/routines">Automate work with routines</see>,
/// "Trigger a routine through the API".) The endpoint is a research preview, under a dated
/// beta header.
/// </summary>
public sealed class RoutineClient(HttpClient http)
{
    private const string Prefix = "https://api.anthropic.com/v1/claude_code/routines/";
    private const string Beta = "experimental-cc-routine-2026-04-01";
    private const string Version = "2023-06-01";

    private readonly HttpClient _http = http;

    /// <summary>A fire URL as the routine's API trigger shows it: only Anthropic's routines endpoint.</summary>
    public static bool IsFireUrl(string? url) =>
        url is not null && url.StartsWith(Prefix, StringComparison.Ordinal) && url.EndsWith("/fire", StringComparison.Ordinal) &&
        Uri.TryCreate(url, UriKind.Absolute, out _);

    /// <summary>Starts one run with <paramref name="text"/> as its payload.</summary>
    public async Task<RoutineFire> FireAsync(string fireUrl, string token, string text, CancellationToken ct)
    {
        if (!IsFireUrl(fireUrl)) throw new RoutineException("The routine's fire URL isn't an Anthropic routines address.");

        using var request = new HttpRequestMessage(HttpMethod.Post, fireUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", Beta);
        request.Headers.TryAddWithoutValidation("anthropic-version", Version);
        request.Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RoutineException($"Routine fire failed: {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return new RoutineFire(
            r.TryGetProperty("claude_code_session_id", out var id) ? id.GetString() ?? "" : "",
            r.TryGetProperty("claude_code_session_url", out var url) ? url.GetString() ?? "" : "");
    }
}
