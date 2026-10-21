using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using rNotice.Core.Rules;

namespace rNotice.Core.Graph;

/// <summary>Supplies a Graph access token. The agent backs this with MSAL and the Windows broker.</summary>
public delegate Task<string> TokenProvider(CancellationToken ct);

public sealed class GraphException(int status, string? code, string message)
    : Exception($"{status} {code}: {message}")
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public bool IsNotFound => Status == 404;
}

/// <summary>
/// Thin Microsoft Graph client: auth header, UTC preference, paging, and retry on throttling.
///
/// Retries are per request and short — a few attempts, honouring <c>Retry-After</c> exactly.
/// Backoff between polls is the agent's job. Never a tight loop.
/// </summary>
public sealed class GraphClient(HttpClient http, TokenProvider token)
{
    public const string Base = "https://graph.microsoft.com/v1.0";
    private const int MaxAttempts = 4;

    private readonly HttpClient _http = http;
    private readonly TokenProvider _token = token;

    public Task<JsonElement> GetAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct) =>
        SendAsync(method, path, () => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path.StartsWith("https://") ? path : Base + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct).ConfigureAwait(false));
            // Every timestamp in UTC. Mailbox time zone differences are a documented source of
            // results that do not line up.
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
            request.Content = content?.Invoke();

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();

            var status = (int)response.StatusCode;
            if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay(response, attempt), ct).ConfigureAwait(false);
                continue;
            }

            var (code, message) = ParseError(text);
            throw new GraphException(status, code, message ?? response.ReasonPhrase ?? "Request failed");
        }
    }

    /// <summary>Follows <c>@odata.nextLink</c> to exhaustion. Stopping early silently drops data.</summary>
    public async IAsyncEnumerable<JsonElement> GetPagedAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = path;
        while (next is not null)
        {
            var page = await GetAsync(next, ct).ConfigureAwait(false);
            foreach (var item in page.GetProperty("value").EnumerateArray())
                yield return item;

            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    public async Task<Me> GetMeAsync(CancellationToken ct)
    {
        var me = await GetAsync("/me?$select=id,mail,userPrincipalName", ct).ConfigureAwait(false);
        var id = me.GetProperty("id").GetString()!;
        var address = me.TryGetProperty("mail", out var mail) && mail.ValueKind == JsonValueKind.String
            ? mail.GetString()!
            : me.GetProperty("userPrincipalName").GetString()!;
        return new Me(id, address);
    }

    // 409 and 423 are Excel's "file busy", seen briefly after an upload.
    private static bool IsTransient(HttpStatusCode s) =>
        s is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.Conflict or HttpStatusCode.Locked;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static (string? Code, string? Message) ParseError(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error))
                return (error.TryGetProperty("code", out var c) ? c.GetString() : null,
                        error.TryGetProperty("message", out var m) ? m.GetString() : null);
        }
        catch (JsonException) { /* not JSON */ }
        return (null, null);
    }
}
