using System.Windows;
using rNotice.Core.Graph;
using rNotice.Core.Items;
using rNotice.Core.Routines;

namespace rNotice.Agent;

/// <summary>
/// The Jira tab (build step 12, ROUTINE-JIRA.md): rNotice → Jira routine → OneDrive → rNotice.
/// A routine of its own, apart from the classify routine, so the routine that reads Teams chats
/// never has the Atlassian connector. rNotice asks only when you open the tab on a list older
/// than <see cref="Jira.Stale"/>, or press Refresh: no background polling, no routine runs you
/// didn't cause.
/// </summary>
internal sealed partial class AgentHost
{
    private const string JiraUrlKey = "jiraRoutine.fireUrl";
    private const string JiraTokenKey = "jiraRoutine.token";
    private const string JiraSnapshotKey = "jira.tickets";

    private static readonly TimeSpan JiraCheckInterval = TimeSpan.FromSeconds(20);

    private (Guid Id, DateTimeOffset SentUtc)? _jiraRequest;
    private DateTimeOffset _nextJiraCheckUtc;
    private bool _jiraChecking;
    private string _jiraLast = "";
    private JiraSnapshot? _jiraSnapshot;
    private bool _jiraLoaded;

    public bool JiraRoutineConfigured => _store.GetState(JiraUrlKey) is not null && _store.GetState(JiraTokenKey) is not null;

    public string? JiraRoutineFireUrl => _store.GetState(JiraUrlKey);

    public string JiraSite => _settings.JiraUrl;

    /// <summary>Sets the Jira routine's API trigger. Returns an error, or null.</summary>
    public string? SetJiraRoutine(string fireUrl, string token)
    {
        fireUrl = fireUrl.Trim();
        token = token.Trim();
        if (!RoutineClient.IsFireUrl(fireUrl)) return "That isn't a routine fire URL (https://api.anthropic.com/v1/claude_code/routines/…/fire).";
        if (token.Length == 0) return "Paste the token too.";
        if (fireUrl == _store.GetState(RoutineUrlKey)) return "That's the classify routine. The Jira routine is a routine of its own.";
        _store.SetState(JiraUrlKey, fireUrl);
        _store.SetState(JiraTokenKey, token);
        Log.Info("Jira routine: fire URL and token set");
        Changed?.Invoke();
        return null;
    }

    public void ClearJiraRoutine()
    {
        _store.SetState(JiraUrlKey, null);
        _store.SetState(JiraTokenKey, null);
        Log.Info("Jira routine: fire URL and token removed");
        Changed?.Invoke();
    }

    /// <summary>The last list the routine sent, kept encrypted in the store so it's there after a restart.</summary>
    public JiraSnapshot? JiraTickets
    {
        get
        {
            if (!_jiraLoaded)
            {
                _jiraSnapshot = Jira.Deserialize(_store.GetState(JiraSnapshotKey));
                _jiraLoaded = true;
            }
            return _jiraSnapshot;
        }
    }

    /// <summary>A request is out to the Jira routine.</summary>
    public bool JiraAsking => _jiraRequest is not null;

    /// <summary>One line for the tab: what's shown, and whether a newer list is coming.</summary>
    public string JiraStatus
    {
        get
        {
            if (!JiraRoutineConfigured) return "Not set up: tray → Dev view → Jira routine.";
            var shown = JiraTickets is { } s
                ? $"{s.Tickets.Count} ticket(s) · {s.Jql} · from {Display.Time(s.FetchedUtc)}"
                : "No list yet";
            return _jiraRequest is { } r ? $"{shown} · asking the routine since {Display.Time(r.SentUtc)}…" : shown;
        }
    }

    public string JiraLast => _jiraLast;

    /// <summary>The tab was opened: ask for a new list if the one shown is old.</summary>
    public void JiraTabOpened()
    {
        if (JiraTickets is { } s && DateTimeOffset.UtcNow - s.FetchedUtc < Jira.Stale && s.Jql == _settings.JiraJql) return;
        _ = RefreshJiraAsync();
    }

    /// <summary>Fires the Jira routine, unless a request is already out or the hourly cap is reached.</summary>
    public async Task RefreshJiraAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (_jiraRequest is { } out_ && now - out_.SentUtc <= Jira.Timeout) return;
        if (_store.GetState(JiraUrlKey) is not { } fireUrl || _store.GetState(JiraTokenKey) is not { } token) return;
        if (_results is null)
        {
            JiraNote("not signed in yet");
            return;
        }

        while (_routineFires.Count > 0 && now - _routineFires.Peek() >= TimeSpan.FromHours(1)) _routineFires.Dequeue();
        if (_routineFires.Count >= Routine.MaxFiresPerHour)
        {
            JiraNote($"{Routine.MaxFiresPerHour} routine requests in the last hour; try again later");
            return;
        }

        var requestId = Guid.NewGuid();
        _jiraRequest = (requestId, now);
        Changed?.Invoke();
        try
        {
            var fire = await _routineClient.FireAsync(fireUrl, token, Jira.RequestText(requestId, _results.Folder, _settings.JiraJql), CancellationToken.None);
            _routineFires.Enqueue(now);
            _nextJiraCheckUtc = now + JiraCheckInterval;
            JiraNote($"asked {requestId} for '{_settings.JiraJql}' · {fire.SessionUrl}");
        }
        catch (Exception ex) when (ex is RoutineException or HttpRequestException or TaskCanceledException)
        {
            _jiraRequest = null;
            JiraNote($"couldn't ask: {ex.Message}");
        }
        Changed?.Invoke();
    }

    /// <summary>Every <see cref="JiraCheckInterval"/> while a request is out: look for its result file.</summary>
    private void TickJira(DateTimeOffset now)
    {
        if (_jiraRequest is not { } request || _jiraChecking || now < _nextJiraCheckUtc) return;
        if (now - request.SentUtc > Jira.Timeout)
        {
            _jiraRequest = null;
            JiraNote($"no answer to {request.Id} after {Jira.Timeout.TotalMinutes:0} min");
            Changed?.Invoke();
            return;
        }
        _nextJiraCheckUtc = now + JiraCheckInterval;
        _ = CheckJiraAsync(request.Id);
    }

    private async Task CheckJiraAsync(Guid requestId)
    {
        _jiraChecking = true;
        var ct = CancellationToken.None;
        try
        {
            var file = (await _results.ListJsonAsync(ct)).FirstOrDefault(f => Routine.RequestIdOf(f.Name) == requestId);
            if (file is null) return;

            string json;
            try { json = await _results.ReadAsync(file.Id, ct); }
            catch (System.Text.Json.JsonException) { json = ""; }
            ApplyJira(requestId, json);
            try { await _results.DeleteAsync(file.Id, ct); }
            catch (GraphException ex) when (ex.IsNotFound) { }
        }
        catch (GraphException ex) when (ex.IsNotFound) { }
        catch (Exception ex) when (ex is GraphException or HttpRequestException or TaskCanceledException)
        {
            Log.Error("Jira result check failed", ex);
        }
        finally
        {
            _jiraChecking = false;
        }
    }

    private void ApplyJira(Guid requestId, string json)
    {
        if (_jiraRequest?.Id != requestId) return;
        _jiraRequest = null;

        if (Jira.Parse(json, out var error) is not { } result || result.RequestId != requestId)
        {
            JiraNote($"answer {requestId} rejected ({error ?? "requestId inside doesn't match the file name"})");
        }
        else
        {
            _jiraSnapshot = new JiraSnapshot(DateTimeOffset.UtcNow, _settings.JiraJql, result.Tickets);
            _jiraLoaded = true;
            _store.SetState(JiraSnapshotKey, Jira.Serialize(_jiraSnapshot));
            JiraNote($"got {result.Tickets.Count} ticket(s)" + (result.Skipped > 0 ? $", skipped {result.Skipped} incomplete" : ""));
        }
        Changed?.Invoke();
    }

    private void JiraNote(string line)
    {
        _jiraLast = $"{DateTime.Now:HH:mm:ss}  {line}";
        Log.Info("Jira routine: " + line);
    }

    /// <summary>
    /// Plans a ticket: the open item already made from it, or a new one. Nothing is written to
    /// Jira, and Done on the item is only rNotice's Done. Cancelling saves nothing.
    /// </summary>
    public void PlanJira(string key, Window? owner)
    {
        if (Jira.OpenItemFor(_store.List(Stage.Intake, Stage.Planned), key) is { } open)
        {
            Plan(open.Id, owner);
            return;
        }
        if (JiraTickets?.Tickets.FirstOrDefault(t => t.Key == key) is not { } ticket) return;

        var now = DateTimeOffset.UtcNow;
        var draft = Jira.From(ticket, _settings.JiraUrl, now);
        if (ShowPlanDialog(draft, except: Guid.Empty, owner) is not { } dialog) return;

        _store.Save(draft.Plan(dialog.StartUtc, dialog.StartUtc is null ? null : dialog.Minutes,
            dialog.Promote, dialog.Promote ? _settings.PlanAlertLeadMinutes : 0), now);
        Log.Info(dialog.StartUtc is { } start
            ? $"Jira {key} planned for {Display.Time(start)}, {dialog.Minutes} min"
            : $"Jira {key} added with no time");
        Changed?.Invoke();
        _ = PromoteAsync();
    }

    /// <summary>An open rNotice item made from this ticket, for the tab to mark.</summary>
    public Item? JiraItem(string key) => Jira.OpenItemFor(_store.List(Stage.Intake, Stage.Planned), key);
}
