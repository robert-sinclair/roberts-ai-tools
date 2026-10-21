using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace rNotice.Probe;

/// <summary>
/// One read per permission rNotice asks for.
///
/// The point of this build is to find out which permissions were actually consented to, rather
/// than assume the five in the request all came through. Each check takes its own token, so one
/// refused scope reports itself instead of failing the run.
///
/// Every call here is a read. Nothing in this build writes to the mailbox, the calendar or
/// OneDrive.
/// </summary>
internal sealed class Probe
{
    private readonly Auth _auth = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var results = new List<CheckResult>();

        // Sign in once up front. Everything after reuses the broker's cached account, so the
        // later checks are silent unless a scope was never consented.
        var signIn = await _auth.GetTokenAsync(new[] { "User.Read" }, ct).ConfigureAwait(false);
        if (!signIn.Succeeded)
        {
            results.Add(CheckResult.Fail("Sign-in", "—", signIn.Error ?? "Unknown failure"));
            return results;
        }

        results.Add(CheckResult.Pass(
            "Sign-in", "—",
            $"{signIn.Upn}{(signIn.WasInteractive ? " (prompted)" : " (silent)")}"));

        results.Add(await CheckAsync("Profile", "User.Read",
            "/me", "displayName", ct).ConfigureAwait(false));

        var from = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var to = DateTimeOffset.UtcNow.AddDays(7).ToString("o");
        results.Add(await CheckAsync("Calendar", "Calendars.ReadWrite",
            $"/me/calendarView?startDateTime={Uri.EscapeDataString(from)}&endDateTime={Uri.EscapeDataString(to)}&$top=5&$select=subject,start",
            "value", ct, countItems: "events in the next 7 days").ConfigureAwait(false));

        results.Add(await CheckAsync("Mail", "Mail.Read",
            "/me/mailFolders/inbox/messages?$top=5&$select=subject,receivedDateTime,conversationId",
            "value", ct, countItems: "recent inbox messages").ConfigureAwait(false));

        results.Add(await CheckAsync("Teams chats", "Chat.Read",
            "/me/chats?$top=5&$select=id,topic,chatType",
            "value", ct, countItems: "chats visible").ConfigureAwait(false));

        results.Add(await CheckAsync("Working hours", "MailboxSettings.Read",
            "/me/mailboxSettings/workingHours", "daysOfWeek", ct).ConfigureAwait(false));

        results.Add(await CheckAsync("OneDrive folder", "Files.ReadWrite",
            $"/me/drive/root:/{Uri.EscapeDataString(AppConfig.WorkbookFolder)}:/children?$top=10&$select=name,size",
            "value", ct, countItems: $"items in /{AppConfig.WorkbookFolder}").ConfigureAwait(false));

        return results;
    }

    private async Task<CheckResult> CheckAsync(
        string name, string scope, string path, string expectKey,
        CancellationToken ct, string? countItems = null)
    {
        var token = await _auth.GetTokenAsync(new[] { $"https://graph.microsoft.com/{scope}" }, ct)
                               .ConfigureAwait(false);

        if (!token.Succeeded)
            return CheckResult.Fail(name, scope, token.Error ?? "Token request failed");

        using var request = new HttpRequestMessage(HttpMethod.Get, AppConfig.GraphBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        // Keep every timestamp in UTC. Mailbox time zone differences are a documented source of
        // results that do not line up.
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return CheckResult.Fail(name, scope, DescribeHttp(response.StatusCode, body));

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty(expectKey, out var value))
                return CheckResult.Fail(name, scope, $"200 OK but no '{expectKey}' in the response");

            var detail = countItems is null
                ? Summarise(value)
                : $"{(value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0)} {countItems}";

            return CheckResult.Pass(name, scope, detail);
        }
        catch (TaskCanceledException)
        {
            return CheckResult.Fail(name, scope, "Timed out after 30s");
        }
        catch (Exception ex)
        {
            return CheckResult.Fail(name, scope, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Summarise(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "(empty)",
        JsonValueKind.Array => $"{value.GetArrayLength()} item(s)",
        _ => value.ToString()
    };

    /// <summary>Graph's own error text is the useful part; the status code alone rarely is.</summary>
    private static string DescribeHttp(HttpStatusCode status, string body)
    {
        var message = TryGraphMessage(body);

        return status switch
        {
            HttpStatusCode.Unauthorized => $"401 Unauthorized. {message ?? "Token rejected."}",
            HttpStatusCode.Forbidden => $"403 Forbidden. Permission not granted. {message}",
            HttpStatusCode.NotFound => $"404 Not Found. {message ?? "The path does not exist for this account."}",
            HttpStatusCode.TooManyRequests => "429 Throttled. Retry after the Retry-After interval.",
            _ => $"{(int)status} {status}. {message}"
        };
    }

    private static string? TryGraphMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }
        }
        catch (JsonException) { /* not JSON; fall through */ }

        return null;
    }
}

internal sealed record CheckResult(string Name, string Scope, bool Succeeded, string Detail)
{
    public static CheckResult Pass(string name, string scope, string detail) => new(name, scope, true, detail);
    public static CheckResult Fail(string name, string scope, string detail) => new(name, scope, false, detail);
}
