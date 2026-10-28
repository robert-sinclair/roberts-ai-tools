using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using rNotice.Core.Graph;
using rNotice.Core.Items;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;
using rNotice.Core.Sources;
using rNotice.Core.Store;
using rNotice.Core.Sync;

namespace rNotice.Agent;

/// <summary>Last outcome of one source's poll, for the tray and the queue window.</summary>
internal sealed record SourceStatus(string Name, DateTimeOffset? LastOkUtc, string Summary, bool Failing);

/// <summary>One timer for the dev view: when it next runs, and how the last run went.</summary>
internal sealed record DevTimer(string Name, DateTimeOffset? NextUtc, bool Running, string Detail, bool Failing);

/// <summary>
/// Presence as the dev view shows it. The activity is the last once-a-minute reading, held in
/// memory only while a meeting is under way, and dropped when none is.
/// </summary>
internal sealed record DevPresence(bool Enabled, IReadOnlyList<string> MeetingsUnderWay, string? Activity,
    DateTimeOffset? CheckedUtc, DateTimeOffset? NextUtc, bool Running);

/// <summary>
/// Wires the pollers, the store, the scheduler and the windows together.
///
/// Everything runs on the UI thread. Polls are async, so the UI stays responsive, and every
/// store access happens on one thread without locks.
///
/// Step 3 scope: workbook and calendar only, rules-only classification, nothing written back.
/// Takeover-class items (meetings) come up in the alert window until step 4 adds the takeover.
/// </summary>
internal sealed class AgentHost(Application app) : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(6);

    private readonly Application _app = app;

    private AgentSettings _settings = new();
    private AgentAuth _auth = null!;
    private GraphClient _graph = null!;
    private CalendarClient _calendar = null!;
    private WorkbookClient _workbook = null!;
    private MailClient _mail = null!;
    private TeamsClient _teams = null!;
    private PresenceClient _presence = null!;
    private ItemStore _store = null!;
    private Scheduler _scheduler = null!;
    private SourceSync? _sync;
    private Task? _meLookup;
    private Me? _me;
    private Tray _tray = null!;
    private HotKeyWindow? _hotKey;
    private DispatcherTimer? _timer;
    private QueueWindow? _queue;
    private AlertWindow? _alert;

    private IReadOnlyList<BusyBlock> _busy = [];
    private string? _lastWorkbookCTag;
    private string _workbookSummary = "";
    private DateTimeOffset _nextPurge;
    private readonly PollState _calendarPoll = new("Calendar");
    private readonly PollState _workbookPoll = new("Workbook");
    private readonly PollState _mailPoll = new("Mail");
    private readonly PollState _teamsPoll = new("Teams");
    private readonly HashSet<string> _loggedSkips = [];

    public event Action? Changed;

    public IReadOnlyList<SourceStatus> Statuses => Polls.Select(p => p.Status).ToList();

    /// <summary>True while any source is mid-poll.</summary>
    public bool Refreshing => Polls.Any(p => p.Running);

    /// <summary>The sources in use: calendar and workbook always, mail and Teams unless turned off.</summary>
    private IEnumerable<PollState> Polls
    {
        get
        {
            yield return _calendarPoll;
            yield return _workbookPoll;
            if (_settings.MailEnabled) yield return _mailPoll;
            if (_settings.TeamsEnabled) yield return _teamsPoll;
        }
    }

    /// <summary>
    /// When the next poll starts — the sooner of the two sources. Later than a minute away only
    /// when a source is backing off after failures, which is exactly when it's worth seeing.
    /// </summary>
    public DateTimeOffset NextRefreshUtc => Polls.Min(p => p.Next);
    public bool NeedsSignIn => _auth.NeedsSignIn;
    public string? SignedInAs => _auth.Upn;
    public int SnoozeMinutes => _settings.SnoozeMinutes;
    public string WorkbookPath => _workbook.DisplayPath;

    public void Start()
    {
        Directory.CreateDirectory(Paths.Root);
        _settings = AgentSettings.Load();

        // First run of an installed copy turns autostart on. A build running from a source tree
        // doesn't, so a dev build never lands in the user's startup apps.
        if (_settings.StartWithWindows is null && IsInstalledCopy())
        {
            AutoStart.Set(true);
            _settings = _settings with { StartWithWindows = true };
            _settings.Save();
            Log.Info("First run: start with Windows turned on");
        }

        CapturePolicy.Hide = _settings.HideFromCapture;
        _auth = new AgentAuth(ParentWindowHandle);
        _auth.StateChanged += () => Changed?.Invoke();
        _graph = new GraphClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, _auth.GetTokenAsync);
        _calendar = new CalendarClient(_graph);
        _mail = new MailClient(_graph);
        _teams = new TeamsClient(_graph);
        _presence = new PresenceClient(_graph);
        _workbook = new WorkbookClient(_graph, _settings.WorkbookFolder, _settings.WorkbookFile);
        _store = new ItemStore(Paths.Database, new DpapiFieldProtector());
        _scheduler = new Scheduler(_settings.ToSchedulerOptions());

        _tray = new Tray(this);
        Changed += _tray.Refresh;

        _hotKey = new HotKeyWindow(OpenQueue);
        if (!_hotKey.Register())
            Log.Warn($"Hotkey {HotKeyWindow.Description} is taken by another application");

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();
    }

    private static bool IsInstalledCopy() =>
        Environment.ProcessPath?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            StringComparison.OrdinalIgnoreCase) == true;

    // ── Loop ────────────────────────────────────────────────────────────────────────────────

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        _lastTickUtc = now;

        if (_calendarPoll.IsDue(now)) _ = PollAsync(_calendarPoll, PollCalendarAsync, _settings.RefreshInterval);
        if (_workbookPoll.IsDue(now)) _ = PollAsync(_workbookPoll, PollWorkbookAsync, _settings.RefreshInterval);
        if (_settings.MailEnabled && _mailPoll.IsDue(now)) _ = PollAsync(_mailPoll, PollMailAsync, _settings.RefreshInterval);
        if (_settings.TeamsEnabled && _teamsPoll.IsDue(now)) _ = PollAsync(_teamsPoll, PollTeamsAsync, _settings.RefreshInterval);
        if (_settings.PresenceEnabled && now >= _nextPresenceUtc) _ = CheckPresenceAsync(now);

        if (now >= _nextPurge)
        {
            var purged = _store.Purge(now, _settings.RetentionDays);
            if (purged > 0) Log.Info($"Purged {purged} closed item(s) older than {_settings.RetentionDays} days");
            _nextPurge = now + PurgeInterval;
        }

        RunScheduler(now);
    }

    /// <summary>
    /// Firing comes from the local store, never from a poll. A network outage delays seeing new
    /// work; it never delays an alert already known about.
    /// </summary>
    private void RunScheduler(DateTimeOffset now)
    {
        var result = _scheduler.Tick(_store.List(Stage.Intake, Stage.Planned), now, _busy);
        if (result.IsEmpty) return;

        foreach (var item in result.Fired)
            _store.Save(Scheduler.MarkFired(item, now), now);

        var windowed = result.Takeovers.Concat(result.Alerts).ToList();
        if (windowed.Count > 0)
        {
            // Every takeover is logged by name, so a day's count can be read back from the log.
            foreach (var t in result.Takeovers) Log.Info($"Takeover: {t.Title}");
            if (result.Alerts.Count > 0) Log.Info($"Alert: {string.Join("; ", result.Alerts.Select(i => i.Title))}");
            ShowAlert(windowed, takeover: result.Takeovers.Count > 0);
        }

        foreach (var toast in result.Toasts)
            _tray.Notify(toast.Title, toast.From is { } from ? $"{from} · {toast.Classification.Reason}" : Display.When(toast));

        Changed?.Invoke();
    }

    private async Task PollAsync(PollState state, Func<DateTimeOffset, Task<string>> poll, TimeSpan interval)
    {
        state.Running = true;
        Changed?.Invoke();   // so the footer can say "refreshing…"
        var now = DateTimeOffset.UtcNow;
        try
        {
            await EnsureSyncAsync();
            var summary = await poll(now);
            state.Succeeded(now, summary, interval);
        }
        catch (SignInRequiredException)
        {
            state.Failed(now, "Sign-in needed", interval, backoff: false);
        }
        catch (Exception ex)
        {
            var wait = state.Failed(now, Describe(ex), interval, backoff: true, MaxBackoff);
            Log.Error($"{state.Name} poll failed; next try in {wait.TotalMinutes:0.#} min", ex);
        }
        finally
        {
            state.Running = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The rules need to know who "you" are, which needs one call to /me. Both polls start
    /// together, so they share one in-flight call; a failed one is retried by the next poll.
    /// </summary>
    private Task EnsureSyncAsync()
    {
        if (_sync is not null) return Task.CompletedTask;
        if (_meLookup is { IsFaulted: false, IsCanceled: false }) return _meLookup;

        return _meLookup = LookUpMe();

        async Task LookUpMe()
        {
            var me = await _graph.GetMeAsync(CancellationToken.None);
            _me = me;
            Display.MyDomain = me.Address.Contains('@') ? Senders.DomainEntry(me.Address) : null;
            _sync = new SourceSync(_store, new Intake(_settings.Rules, me));
            Log.Info($"Signed in as {me.Address}");
        }
    }

    /// <summary>Local midnight today, or an hour back if that's earlier (just after midnight).</summary>
    private DateTimeOffset CalendarReadFrom(DateTimeOffset now)
    {
        var zone = _settings.ToWorkHours().Zone;
        var midnight = TimeZoneInfo.ConvertTime(now, zone).Date;
        var start = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
        return start < now.AddHours(-1) ? start : now.AddHours(-1);
    }

    private async Task<string> PollCalendarAsync(DateTimeOffset now)
    {
        var readStartedUtc = DateTimeOffset.UtcNow;
        // From the start of today, so meetings already over stay on today's schedule.
        var from = CalendarReadFrom(now);
        var all = await _calendar.GetViewAsync(from, now.AddDays(_settings.Rules.HorizonDays), CancellationToken.None);

        // rNotice's own events are its planned tasks, not meetings. Counted as meetings they'd
        // come into the queue a second time, and every promoted task would clash with itself.
        var category = _settings.Rules.PromotedCategory;
        bool Ours(CalendarEvent e) => e.Categories.Contains(category, StringComparer.OrdinalIgnoreCase);
        var events = all.Where(e => !Ours(e)).ToList();

        _busy = events.Where(e => !e.IsCancelled && !e.IsAllDay)
                      .Select(e => new BusyBlock(e.StartUtc, e.EndUtc, e.ShowAs))
                      .ToList();

        // For planning, anything not marked free takes the time: busy, out of office,
        // tentative, working elsewhere — all-day ones included, so a day off isn't offered.
        _meetingEvents = events.Where(e => !e.IsCancelled && e.ShowAs != ShowAs.Free).ToList();
        _meetingBlocks = _meetingEvents.Select(e => new TimeBlock(e.StartUtc, e.EndUtc)).ToList();

        // A write made while this read was in flight is newer than what the read saw.
        var promoted = all.Where(Ours).ToDictionary(e => e.Id, e => new PromotedEvent(e.Id, e.StartUtc, e.EndUtc));
        foreach (var (id, write) in _eventWrites)
            if (write.AtUtc >= readStartedUtc) promoted[id] = write.Event;
            else _eventWrites.Remove(id);
        _promotedEvents = promoted;
        _calendarWindow = (from, now.AddDays(_settings.Rules.HorizonDays));
        _calendarKnownUtc = now;

        var report = _sync!.SyncCalendar(events, now);
        LogSync("Calendar", report);
        MoveConflictsToIntake(now);
        _ = PromoteAsync();
        // The footer gets the count; what changed goes to the log.
        return $"{events.Count - report.Skipped.Count} meetings";
    }

    private async Task<string> PollWorkbookAsync(DateTimeOffset now)
    {
        var file = await _workbook.GetFileAsync(CancellationToken.None);
        if (file is null)
        {
            Log.Info($"No workbook at {_workbook.DisplayPath}; creating it");
            file = await _workbook.CreateAsync(CancellationToken.None);
            _tray.Notify("Workbook created", $"rNotice created {_workbook.DisplayPath} in your OneDrive.");
        }

        // Unchanged content, nothing to read. The cheap pre-check SPEC.md calls for.
        if (file.CTag is not null && file.CTag == _lastWorkbookCTag)
            return _workbookSummary;

        var rows = await _workbook.ReadRowsAsync(file, CancellationToken.None);
        var report = _sync!.SyncWorkbook(rows, now);
        _lastWorkbookCTag = file.CTag;
        LogSync("Workbook", report);
        return _workbookSummary = $"{rows.Count} {(rows.Count == 1 ? "row" : "rows")}";
    }

    private const string MailDeltaKey = "mail.inbox.deltaLink";
    private static string ChatWatermarkKey(string chatId) => $"teams.watermark.{chatId}";

    private IReadOnlyList<ChatInfo> _chats = [];

    /// <summary>
    /// Lists your chats, then reads each watched one that has a message newer than its
    /// watermark — so a quiet minute costs one or two calls, not one per chat. Watermarks are
    /// saved only after the messages are stored, so a failure re-reads rather than loses them.
    /// </summary>
    private async Task<string> PollTeamsAsync(DateTimeOffset now)
    {
        var me = _me!;
        _chats = await _teams.ListChatsAsync(me.UserId, CancellationToken.None);

        var rules = _settings.Rules;
        var watched = _chats.Where(c => (c.Type == ChatType.OneOnOne && rules.WatchOneOnOneChats) || rules.WatchedChatIds.Contains(c.Id)).ToList();

        var messages = new List<ChatMessage>();
        var marks = new List<(string Key, DateTimeOffset Mark)>();
        foreach (var chat in watched)
        {
            var key = ChatWatermarkKey(chat.Id);
            var since = _store.GetState(key) is { } saved && DateTimeOffset.TryParse(saved, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : now.AddHours(-_settings.TeamsBackfillHours);
            if (chat.LastMessageUtc is { } last && last <= since) continue;

            var (found, mark) = await _teams.GetMessagesSinceAsync(chat, since, CancellationToken.None);
            messages.AddRange(found);
            marks.Add((key, mark));
        }

        if (messages.Count > 0)
        {
            var report = _sync!.SyncTeams(messages, now);
            LogSync("Teams", report);
        }
        foreach (var (key, mark) in marks)
            _store.SetState(key, mark.ToString("o", CultureInfo.InvariantCulture));

        var open = _store.List(Stage.Intake).Count(i => i.Source == Source.Teams);
        return $"{watched.Count} chats watched, {open} in Intake";
    }

    // ── Watched chats ───────────────────────────────────────────────────────────────────────

    public bool WatchOneOnOneChats => _settings.Rules.WatchOneOnOneChats;
    public IReadOnlyList<string> WatchedChatIds => _settings.Rules.WatchedChatIds;

    /// <summary>Your chats, read fresh for the picker.</summary>
    public async Task<IReadOnlyList<ChatInfo>> ListChatsAsync()
    {
        await EnsureSyncAsync();
        return _chats = await _teams.ListChatsAsync(_me!.UserId, CancellationToken.None);
    }

    /// <summary>
    /// Saves the picker. A newly watched chat is read back <c>teamsBackfillHours</c>, quietly.
    /// A chat taken off keeps its items; it just stops adding new ones.
    /// </summary>
    public void SetWatchedChats(IReadOnlyList<string> chatIds, bool oneOnOne)
    {
        var updated = _settings with { Rules = _settings.Rules with { WatchedChatIds = chatIds, WatchOneOnOneChats = oneOnOne } };
        if (!updated.Save())
        {
            _tray.Notify("Watched chats not saved", "settings.json couldn't be read or written. See the log.");
            return;
        }
        _settings = updated;
        if (_me is { } me) _sync = new SourceSync(_store, new Intake(_settings.Rules, me));
        Log.Info($"Watched Teams chats: {chatIds.Count} picked; one-on-one chats {(oneOnOne ? "watched" : "not watched")}");
        _teamsPoll.Now();
        Tick();
    }

    private WatchedChatsWindow? _chatsWindow;

    public void OpenWatchedChats()
    {
        if (_chatsWindow is { IsLoaded: true })
        {
            _chatsWindow.Activate();
            return;
        }
        _chatsWindow = new WatchedChatsWindow(this);
        _chatsWindow.Closed += (_, _) => _chatsWindow = null;
        _chatsWindow.Show();
    }
    private const string ReadStateBackfillKey = "mail.readState.backfilled";
    private const string PortalRescanKey = "caseLinks.portalRescan";

    /// <summary>
    /// Sent Items first, so a reply that lands right after you sent is matched; then the inbox
    /// changes since the last round. The deltaLink is saved only after the round is stored, so
    /// a crash mid-poll repeats the round rather than skipping it.
    /// </summary>
    private async Task<string> PollMailAsync(DateTimeOffset now)
    {
        // Items stored before 0.4.2 have no read state. Re-read the backfill window once so
        // they get it; ingest merges, so nothing is duplicated and nothing fires.
        if (_store.GetState(ReadStateBackfillKey) is null)
        {
            _store.SetState(MailDeltaKey, null);
            _store.SetState(ReadStateBackfillKey, "done");
        }

        var firstSent = await _mail.GetFirstSentByConversationAsync(now.AddDays(-_settings.ReplyLookbackDays), CancellationToken.None);
        var delta = await _mail.GetInboxChangesAsync(_store.GetState(MailDeltaKey), now.AddDays(-_settings.MailBackfillDays), CancellationToken.None);

        if (delta.Restarted)
            Log.Info($"Mail: reading the inbox from the last {_settings.MailBackfillDays} days");

        var report = _sync!.SyncMail(delta.Changed, delta.RemovedIds, firstSent, now, delta.ReadChanges);
        _store.SetState(MailDeltaKey, delta.DeltaLink);
        LogSync("Mail", report);

        // Counts by reason, never subjects: enough to see the filters working on a first read.
        if (delta.Restarted && report.Skipped.Count > 0)
            Log.Info("Mail skipped: " + string.Join(", ", report.Skipped
                .GroupBy(s => s.Reason).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}")));

        await LinkCasesAsync(now);

        var open = _store.List(Stage.Intake).Count(i => i.Source == Source.Mail);
        return $"{open} in Intake";
    }

    /// <summary>
    /// For open mail about a Salesforce case, finds the case's record ID in a Salesforce link —
    /// in the excerpt if it's there, otherwise by reading that one message's body — and stores
    /// the internal Lightning link. The body isn't kept. Each item is looked at once (an empty
    /// link means "looked, none"), at most ten a poll so a backlog doesn't stall the refresh.
    /// </summary>
    private async Task LinkCasesAsync(DateTimeOffset now)
    {
        // 0.8.5 learned the customer-portal link. Items already looked at and found to have no
        // link get looked at once more.
        if (_store.GetState(PortalRescanKey) is null)
        {
            foreach (var looked in _store.List(Stage.Intake, Stage.Planned).Where(i => i.Source == Source.Mail && i.CaseUrl == ""))
                _store.Save(looked with { CaseUrl = null }, now);
            _store.SetState(PortalRescanKey, "done");
        }

        var pending = _store.List(Stage.Intake, Stage.Planned)
            .Where(i => i.Source == Source.Mail && i.CaseUrl is null &&
                        (CaseLinks.FindCaseNumber(i.Title) ?? CaseLinks.FindCaseNumber(i.Snippet)) is not null)
            .Take(10)
            .ToList();
        if (pending.Count == 0) return;

        var found = 0;
        foreach (var item in pending)
        {
            var id = CaseLinks.FindRecordId(item.Snippet);
            if (id is null)
            {
                try { id = CaseLinks.FindRecordId(await _mail.GetBodyAsync(item.SourceId, CancellationToken.None)); }
                catch (GraphException ex) when (ex.IsNotFound) { /* gone from the mailbox */ }
            }

            // Re-read: the await gave you time to plan or dismiss it, and that must not be undone.
            if (_store.Get(item.Id) is not { } current) continue;
            var url = id is null ? "" : CaseLinks.LightningUrl(_settings.SalesforceUrl, id);
            _store.Save(current with { CaseUrl = url }, now);
            if (id is not null) found++;
        }
        Log.Info($"Salesforce: linked {found} of {pending.Count} case email(s)");
    }

    /// <summary>Each distinct skip is logged once, not every poll.</summary>
    private void LogSync(string source, SyncReport report)
    {
        if (report.Inserted + report.Updated + report.Removed + report.Closed > 0)
            Log.Info($"{source}: {report}");

        foreach (var (id, reason) in report.Skipped)
            if (_loggedSkips.Add($"{source}|{id}|{reason}") && source == "Workbook")
                Log.Info($"Workbook row {id} skipped: {reason}");
    }

    private static string Describe(Exception ex) => ex switch
    {
        GraphException g => $"Graph {g.Status} {g.Code}",
        WorkbookSchemaException w => w.Message,
        HttpRequestException => "Network unreachable",
        TaskCanceledException => "Timed out",
        _ => ex.GetType().Name
    };

    // ── Actions ─────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<Item> Items() => _store.List();

    /// <summary>
    /// Done, dismissed or back to Intake also ends a working session, banking its time. A task
    /// done before its planned block started loses the plan: the slot is free again. Done once
    /// it had started keeps its block, on the schedule and in Outlook, as a record.
    /// </summary>
    public void SetStage(Guid id, Stage stage)
    {
        var now = DateTimeOffset.UtcNow;
        Update(id, i =>
        {
            var changed = (stage == Stage.Planned ? i : i.StopWork(now)) with { Stage = stage };
            return stage == Stage.Done && i.Source != Source.Calendar && i.PlannedStartUtc > now
                ? changed with { PlannedStartUtc = null, PlannedMinutes = null }
                : changed;
        });
    }

    // ── Working on it ───────────────────────────────────────────────────────────────────────

    /// <summary>"I'm working on this": alerts stop until the check-in; the plan stays put.</summary>
    public void StartWork(Guid id)
    {
        // The check-in comes no later than the next plan's alert: that's where you hand over.
        Update(id, i => i.StartWork(DateTimeOffset.UtcNow, _settings.PlanMinutes) is var w && w.FireAtUtc is { } checkIn
            ? w with { FireAtUtc = Overrun.CheckIn(_store.List(), w, checkIn) }
            : w);
        if (_store.Get(id) is { FireAtUtc: { } checkIn } item)
            Log.Info($"Working on '{item.Title}'; check-in {Display.Time(checkIn)}");
    }

    public static readonly int[] KeepGoingMinutes = [15, 30, 60];

    /// <summary>
    /// "Still working" for <paramref name="minutes"/> more. A planned task's block grows by that
    /// much (its Outlook event too) and the plans after it that it now lands on move along
    /// (<see cref="Overrun"/>), each with a note. An unplanned one just checks in again later.
    /// </summary>
    public void KeepWorking(Guid id, int minutes)
    {
        if (_store.Get(id) is not { } item) return;
        var now = DateTimeOffset.UtcNow;
        var newEnd = RunOverEnd(item, minutes, now);

        foreach (var push in Pushes(item, newEnd))
        {
            var task = push.Task;
            var promote = task.PromoteToCalendar;
            _store.Save(task.Plan(push.StartUtc, task.PlannedMinutes, promote, promote ? _settings.PlanAlertLeadMinutes : 0) with
            {
                Note = $"Pushed to {Display.Time(push.StartUtc)}: '{item.Title}' ran over", NoteLink = null
            }, now);
            Log.Info($"'{task.Title}' pushed to {Display.Time(push.StartUtc)} ('{item.Title}' ran over)");
        }

        var extended = item.RunOver(newEnd, newEnd);
        Update(id, i => i.RunOver(newEnd, Overrun.CheckIn(_store.List(), extended, newEnd)));
        Log.Info($"Still working on '{item.Title}' until {Display.Time(newEnd)}");
    }

    /// <summary>The "Still working" choices, each saying what it would move: "30 min · moves 2 plans".</summary>
    public IReadOnlyList<(int Minutes, string Text)> KeepGoingChoices(Guid id)
    {
        if (_store.Get(id) is not { } item) return [];
        var now = DateTimeOffset.UtcNow;
        var blockEnd = item.PlannedStartUtc?.AddMinutes(item.PlannedMinutes ?? _settings.PlanMinutes);
        return KeepGoingMinutes.Select(m =>
        {
            var newEnd = RunOverEnd(item, m, now);
            var parts = new List<string> { Display.Duration(TimeSpan.FromMinutes(m)) };
            if (Pushes(item, newEnd).Count is var n and > 0) parts.Add($"moves {n} plan{(n == 1 ? "" : "s")}");
            if (blockEnd is { } from && _meetingEvents.FirstOrDefault(e => e.StartUtc < newEnd && e.EndUtc > from) is { } clash)
                parts.Add($"into {clash.Subject}");
            return (m, string.Join(" · ", parts));
        }).ToList();
    }

    /// <summary>
    /// Where a planned block ends after <paramref name="minutes"/> more: that much past its
    /// planned end, so 9–10 plus 30 min is 9–10:30 even if you answer at 10:02. Answered well
    /// after the block ended, it's that long from now, on the quarter hour.
    /// </summary>
    private DateTimeOffset RunOverEnd(Item item, int minutes, DateTimeOffset now)
    {
        var length = TimeSpan.FromMinutes(minutes);
        if (item.PlannedStartUtc is not { } start) return now + length;
        var end = start.AddMinutes(item.PlannedMinutes ?? _settings.PlanMinutes) + length;
        return end < now + length - TimeSpan.FromMinutes(5) ? FreeSlots.RoundUp(now + length, _settings.ToWorkHours().Zone) : end;
    }

    private IReadOnlyList<Push> Pushes(Item item, DateTimeOffset newEnd) =>
        Overrun.Pushes(_store.List(Stage.Planned), item, newEnd, _settings.ToWorkHours(), _meetingBlocks, _settings.PlanMinutes);

    /// <summary>The plan a working item's check-in offers to start instead.</summary>
    public Item? NextUp(Guid id) =>
        _store.Get(id) is { IsWorking: true } item ? Overrun.NextUp(_store.List(Stage.Planned), item, _settings.PlanMinutes) : null;

    /// <summary>
    /// Moves on to the next plan without finishing this one: its time is banked and it goes back
    /// to Intake with a note, to plan again; the next one starts.
    /// </summary>
    public void SwitchTo(Guid fromId, Guid toId)
    {
        if (_store.Get(fromId) is not { } from || _store.Get(toId) is not { } to) return;
        var now = DateTimeOffset.UtcNow;
        Update(fromId, i => i.StopWork(now) is var stopped
            ? stopped.BackToIntake($"Paused for '{to.Title}' · worked {Display.Duration(stopped.Worked(now))}", now)
            : i);
        StartWork(toId);
        Log.Info($"Switched from '{from.Title}' to '{to.Title}'");
    }

    /// <summary>Stops the session without finishing: the time is banked, alerts resume as planned.</summary>
    public void StopWork(Guid id) =>
        Update(id, i => i.StopWork(DateTimeOffset.UtcNow) is var stopped && stopped.PlannedStartUtc is { } start && start > DateTimeOffset.UtcNow
            ? stopped with { FireAtUtc = start, LastFiredUtc = null }
            : stopped with { LastFiredUtc = DateTimeOffset.UtcNow });

    /// <summary>
    /// Your own title for an item. It survives later polls of the source; blank restores the
    /// source's on the next one. An Outlook event rNotice made for it is renamed to match.
    /// </summary>
    public async void SetTitle(Guid id, string title)
    {
        if (_store.Get(id) is not { } before || before.Source == Source.Calendar) return;
        Update(id, i => i.Rename(title));

        if (_store.Get(id) is { CalendarEventId: { } eventId } after && after.Title != before.Title)
        {
            try
            {
                await _calendar.RenameEventAsync(eventId, after.Title, CancellationToken.None);
                Log.Info($"Calendar: renamed the event for '{after.Title}'");
            }
            catch (Exception ex)
            {
                Log.Error($"Calendar: couldn't rename the event for '{after.Title}'", ex);
            }
        }
    }

    /// <summary>
    /// Asks "when will you do it?" and plans the item. Every way of planning comes through here —
    /// P, the right-click menu, a drag from Intake, the alert's Plan button — so they all behave
    /// the same. Cancel changes nothing.
    /// </summary>
    public void Plan(Guid id, Window? owner)
    {
        if (_store.Get(id) is not { } item) return;

        var hours = _settings.ToWorkHours();
        var dialog = new PlanDialog(item, _settings.PlanMinutes, item.PlannedStartUtc is null ? _settings.PromoteByDefault : item.PromoteToCalendar,
            minutes => FreeSlots.Find(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes), hours, BlockedTime(except: id)),
            _calendarKnownUtc, hours,
            day => DayGrid.Build(day, hours, AgendaFor(day, except: id), DateTimeOffset.UtcNow));
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.Topmost = owner.Topmost;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        if (dialog.ShowDialog() != true) return;

        // Re-planning means doing it later: a session in progress ends here.
        Update(id, i => i.StopWork(DateTimeOffset.UtcNow).Plan(dialog.StartUtc, dialog.StartUtc is null ? null : dialog.Minutes,
            dialog.Promote, dialog.Promote ? _settings.PlanAlertLeadMinutes : 0));
        if (dialog.StartUtc is { } start)
            Log.Info($"'{item.Title}' planned for {Display.Time(start)}, {dialog.Minutes} min");
    }

    private IReadOnlyList<TimeBlock> _meetingBlocks = [];
    private IReadOnlyList<CalendarEvent> _meetingEvents = [];

    /// <summary>
    /// One working day's meetings and other plans, with titles, for the Plan dialog's day view.
    /// Blocklisted meetings are included: they don't alert, but they still take the time.
    /// </summary>
    /// <summary>
    /// A meeting now sits on a task's planned slot — booked or moved there since you planned.
    /// The task goes back to Intake with a note saying which meeting and when, and a
    /// notification, so it isn't silently double-booked.
    /// </summary>
    private void MoveConflictsToIntake(DateTimeOffset now)
    {
        var meetings = _meetingEvents.Select(e => new AgendaEntry(e.StartUtc, e.EndUtc, e.Subject, IsMeeting: true, EventId: e.Id));
        var zone = _settings.ToWorkHours().Zone;
        foreach (var (task, meeting) in PlanConflicts.Find(_store.List(Stage.Planned), meetings, now, _settings.PlanMinutes))
        {
            var note = PlanConflicts.Note(task, meeting, zone, now);
            _store.Save(task.BackToIntake(note, now, meeting.EventId), now);
            Log.Info($"'{task.Title}' moved back to Intake: clashes with '{meeting.Title}'");
            _tray.Notify("Moved back to Intake", $"{task.Title} — clashed with {meeting.Title}.");
        }

        // Notes 0.6.0 wrote in the long form: shorten them, and link the meeting if it's still on.
        foreach (var item in _store.List(Stage.Intake).Where(i => i.NoteLink is null))
        {
            if (!PlanConflicts.TryShorten(item.Note, out var shortNote, out var title)) continue;
            var meeting = _meetingEvents.FirstOrDefault(e => e.Subject == title);
            _store.Save(item with { Note = shortNote, NoteLink = meeting?.Id }, now);
        }
    }

    /// <summary>A meeting's item, by calendar event id — for a note's link.</summary>
    public Item? MeetingItem(string eventId) => _store.GetBySource(Source.Calendar, eventId);

    /// <summary>When a calendar event starts, if it's in the calendar window rNotice has read.</summary>
    public DateTimeOffset? EventStart(string eventId) => _meetingEvents.FirstOrDefault(e => e.Id == eventId)?.StartUtc;

    // ── Planned calendar view ───────────────────────────────────────────────────────────────

    public WorkHours WorkHours => _settings.ToWorkHours();

    /// <summary>
    /// Everything on one day for the Planned calendar: your meetings (with their items, so
    /// they can be selected and acted on), meetings rNotice doesn't track — blocklisted, done or
    /// dismissed — as plain busy time, and your planned tasks. All-day events are flagged for the
    /// strip at the top.
    /// </summary>
    public IReadOnlyList<CalendarEntry> CalendarDay(DateOnly day)
    {
        var zone = _settings.ToWorkHours().Zone;
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
        var to = from.AddDays(1);

        var entries = new List<CalendarEntry>();
        foreach (var e in _meetingEvents.Where(e => e.StartUtc < to && e.EndUtc > from))
        {
            // Done and dismissed meetings stay clickable — grey, but they still open in the
            // detail pane. Only blocklisted ones have no item behind them.
            var item = _store.GetBySource(Source.Calendar, e.Id);
            entries.Add(new CalendarEntry(item, e.Subject, e.StartUtc, e.EndUtc,
                item is { IsOpen: true } ? CalendarKind.Meeting : CalendarKind.Busy, e.IsAllDay));
        }

        // Done tasks keep their block, greyed, so the day shows what you did as well as what's left.
        foreach (var task in _store.List(Stage.Planned, Stage.Done).Where(i => i.Source != Source.Calendar &&
                     i.PlannedStartUtc is { } s && s < to && s.AddMinutes(i.PlannedMinutes ?? _settings.PlanMinutes) > from))
        {
            var start = task.PlannedStartUtc!.Value;
            entries.Add(new CalendarEntry(task, task.Title, start, start.AddMinutes(task.PlannedMinutes ?? _settings.PlanMinutes),
                task.Stage == Stage.Done ? CalendarKind.DoneTask : CalendarKind.Task, false));
        }
        return entries;
    }

    /// <summary>Planned, but with no time: listed above the calendar whatever the day.</summary>
    public IReadOnlyList<Item> PlannedWithoutTime() =>
        _store.List(Stage.Planned).Where(i => i.Source != Source.Calendar && i.PlannedStartUtc is null).ToList();

    /// <summary>
    /// Plans an item at a time it was dropped on in the calendar, for the default length. If that
    /// would land on a meeting or another plan, the Plan dialog opens instead so you can choose.
    /// </summary>
    public void PlanAt(Guid id, DateTimeOffset startUtc, Window owner)
    {
        if (_store.Get(id) is not { } item) return;
        var end = startUtc.AddMinutes(_settings.PlanMinutes);
        if (BlockedTime(except: id).Any(b => b.Overlaps(startUtc, end)) || end <= DateTimeOffset.UtcNow)
        {
            Plan(id, owner);
            return;
        }
        Update(id, i => i.Plan(startUtc, _settings.PlanMinutes, _settings.PromoteByDefault,
            _settings.PromoteByDefault ? _settings.PlanAlertLeadMinutes : 0));
        Log.Info($"'{item.Title}' planned for {Display.Time(startUtc)}, {_settings.PlanMinutes} min (dropped on the calendar)");
    }

    private IReadOnlyList<AgendaEntry> AgendaFor(DateOnly day, Guid except)
    {
        var zone = _settings.ToWorkHours().Zone;
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var to = from.AddDays(1);

        var meetings = _meetingEvents
            .Where(e => e.StartUtc < to && e.EndUtc > from)
            .Select(e => new AgendaEntry(e.StartUtc, e.EndUtc, e.Subject, IsMeeting: true));

        var plans = _store.List(Stage.Planned)
            .Where(i => i.Id != except && i.Source != Source.Calendar && i.PlannedStartUtc is { } s && s < to && s >= from.AddHours(-12))
            .Select(i => new AgendaEntry(i.PlannedStartUtc!.Value,
                i.PlannedStartUtc.Value.AddMinutes(i.PlannedMinutes ?? _settings.PlanMinutes), i.Title, IsMeeting: false));

        return meetings.Concat(plans).ToList();
    }
    private DateTimeOffset? _calendarKnownUtc;

    /// <summary>
    /// Meetings, plus everything else you've planned a time for — so a second plan doesn't land
    /// on the first. The item being planned is left out, so re-planning can keep its own slot.
    /// </summary>
    private IEnumerable<TimeBlock> BlockedTime(Guid except) =>
        _meetingBlocks.Concat(_store.List(Stage.Planned)
            .Where(i => i.Id != except && i.Source != Source.Calendar && i.PlannedStartUtc is not null)
            .Select(i => new TimeBlock(i.PlannedStartUtc!.Value,
                i.PlannedStartUtc.Value.AddMinutes(i.PlannedMinutes ?? _settings.PlanMinutes))));

    // ── Presence (step 11) ──────────────────────────────────────────────────────────────────

    private static readonly TimeSpan PresenceInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _nextPresenceUtc;
    private bool _presenceRunning;
    private string? _lastActivity;
    private DateTimeOffset? _lastActivityUtc;

    // ── Dev view ────────────────────────────────────────────────────────────────────────────

    private DateTimeOffset _lastTickUtc;

    /// <summary>Every timer rNotice runs on, for the dev view's countdowns.</summary>
    public IReadOnlyList<DevTimer> DevTimers()
    {
        static DateTimeOffset? When(DateTimeOffset t) => t == DateTimeOffset.MinValue ? null : t;
        var timers = Polls.Select(p => new DevTimer(p.Name, When(p.Next), p.Running,
            (p.Status.LastOkUtc is { } ok ? $"last OK {ok.ToLocalTime():HH:mm:ss} · " : "") + p.Status.Summary,
            p.Status.Failing)).ToList();

        var underWay = Presence.UnderWay(_store.List(Stage.Planned), DateTimeOffset.UtcNow).Count > 0;
        timers.Add(new DevTimer("Presence", _settings.PresenceEnabled && underWay ? When(_nextPresenceUtc) : null, _presenceRunning,
            !_settings.PresenceEnabled ? "off (presenceEnabled)" : underWay ? "once a minute while a meeting is under way" : "idle: no meeting under way",
            false));
        timers.Add(new DevTimer("Alerts", When(_lastTickUtc) is { } t ? t + TickInterval : null, false, "scheduler, every 5 s", false));
        timers.Add(new DevTimer("Cleanup", When(_nextPurge), false, $"closed items older than {_settings.RetentionDays} days", false));
        return timers;
    }

    public DevPresence DevPresenceNow()
    {
        var meetings = Presence.UnderWay(_store.List(Stage.Planned), DateTimeOffset.UtcNow)
            .Select(m => m.JoinedUtc is { } j ? $"{m.Title} (joined {j.ToLocalTime():HH:mm})" : m.Title).ToList();
        return new DevPresence(_settings.PresenceEnabled, meetings, _lastActivity, _lastActivityUtc,
            meetings.Count > 0 ? _nextPresenceUtc : null, _presenceRunning);
    }

    private DevViewWindow? _devView;

    public void OpenDevView()
    {
        if (_devView is { IsLoaded: true })
        {
            _devView.Activate();
            return;
        }
        _devView = new DevViewWindow(this);
        _devView.Closed += (_, _) => _devView = null;
        _devView.Show();
    }

    /// <summary>
    /// Once a minute, and only while a meeting is under way and not marked joined, reads your
    /// own presence. A call or presenting marks that meeting joined, as Join in rNotice does.
    /// The activity is used and dropped: never logged, never stored.
    /// </summary>
    private async Task CheckPresenceAsync(DateTimeOffset now)
    {
        if (_presenceRunning) return;
        if (Presence.UnderWay(_store.List(Stage.Planned), now).Count == 0)
        {
            _lastActivity = null;   // no meeting: nothing held
            _lastActivityUtc = null;
            return;
        }
        _presenceRunning = true;
        _nextPresenceUtc = now + PresenceInterval;
        try
        {
            var activity = await _presence.GetActivityAsync(CancellationToken.None);
            _lastActivity = activity;
            _lastActivityUtc = DateTimeOffset.UtcNow;
            if (Presence.MeetingJoined(_store.List(Stage.Planned), DateTimeOffset.UtcNow, activity) is { } meeting)
            {
                Update(meeting.Id, i => i with { JoinedUtc = DateTimeOffset.UtcNow });
                Log.Info($"Joined '{meeting.Title}' (Teams shows you in a call)");
            }
        }
        catch (SignInRequiredException) { }
        catch (Exception ex)
        {
            Log.Error("Presence check failed", ex);
        }
        finally
        {
            _presenceRunning = false;
        }
    }

    /// <summary>
    /// Tray → Check Teams presence: what Teams says right now and whether rNotice would count it
    /// as joined. Shown, not stored — for checking the mapping during a real meeting. A window,
    /// not a notification: Do Not Disturb and Focus silently drop notifications, and a check you
    /// asked for must always answer.
    /// </summary>
    public async Task<string> CheckPresenceNowAsync()
    {
        try
        {
            var activity = await _presence.GetActivityAsync(CancellationToken.None) ?? "(none)";
            return Presence.IsInACall(activity)
                ? $"{activity}: counts as in a call, so a meeting under way is marked joined."
                : $"{activity}: not a call, so nothing is marked joined.";
        }
        catch (Exception ex)
        {
            Log.Error("Presence check failed", ex);
            return "Couldn't read your presence. The log has the details.";
        }
    }

    /// <summary>Opens the Teams call and notes that you joined through rNotice.</summary>
    public void Join(Guid id)
    {
        if (_store.Get(id) is not { JoinUrl: { } url }) return;
        OpenLink(url);
        Update(id, i => i with { JoinedUtc = DateTimeOffset.UtcNow });
    }

    /// <summary>The alert's "Rebook in" choices. The item's own current slot counts as free.</summary>
    public IReadOnlyList<RebookChoice> RebookChoices(Guid id) =>
        Rebook.Choices(DateTimeOffset.UtcNow, _settings.ToWorkHours(), BlockedTime(except: id).ToList());

    /// <summary>
    /// Plans the item into the chosen slot. A plan already on your Outlook calendar stays there
    /// (its event moves); a new one follows <c>promoteByDefault</c>, as a drag does.
    /// </summary>
    public void RebookAt(Guid id, RebookChoice choice)
    {
        if (_store.Get(id) is not { } item) return;
        var promote = item.Stage == Stage.Planned && item.PlannedStartUtc is not null ? item.PromoteToCalendar : _settings.PromoteByDefault;
        Update(id, i => i.StopWork(DateTimeOffset.UtcNow).Plan(choice.StartUtc, choice.Minutes, promote, promote ? _settings.PlanAlertLeadMinutes : 0));
        Log.Info($"'{item.Title}' booked for {Display.Time(choice.StartUtc)}, {choice.Minutes} min (from the alert)");
    }

    public void Snooze(Guid id) =>
        Update(id, i => i.Snooze(DateTimeOffset.UtcNow.AddMinutes(_settings.SnoozeMinutes)));

    public int MeetingSnoozeSeconds => _settings.MeetingSnoozeSeconds;

    /// <summary>
    /// Takes a meeting's alert and dim away until <c>meetingSnoozeSeconds</c> before it starts,
    /// then brings both back. Does nothing if that moment has already passed, or if the real
    /// alert hasn't fired yet — a preview — so previewing never moves the real takeover.
    /// </summary>
    public void SnoozeMeeting(Guid id)
    {
        if (_store.Get(id) is not { DueUtc: { } start } item) return;
        if (item.LastFiredUtc is null && item.FireAtUtc > DateTimeOffset.UtcNow) return;
        var until = start.AddSeconds(-_settings.MeetingSnoozeSeconds);
        var wait = until - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero) return;

        Update(id, i => i.Snooze(until));

        // The loop ticks every 5 seconds; a one-off tick brings it back on time, not up to 5 s late.
        var timer = new DispatcherTimer { Interval = wait };
        timer.Tick += (_, _) => { timer.Stop(); Tick(); };
        timer.Start();
    }

    private void Update(Guid id, Func<Item, Item> change)
    {
        if (_store.Get(id) is not { } item) return;
        var updated = change(item);
        _store.Save(updated, DateTimeOffset.UtcNow);
        Log.Info($"{updated.Source} item '{updated.Title}' → {updated.Stage}" +
                 (updated.SnoozedUntilUtc != item.SnoozedUntilUtc && updated.SnoozedUntilUtc is { } until
                     ? $", snoozed until {until.ToLocalTime():HH:mm:ss}" : ""));
        Changed?.Invoke();
        _ = PromoteAsync();
    }

    // ── Calendar promotion (step 8) ─────────────────────────────────────────────────────────

    private Dictionary<string, PromotedEvent> _promotedEvents = [];
    private (DateTimeOffset From, DateTimeOffset To) _calendarWindow;
    private bool _promoting, _promoteAgain;
    private readonly Dictionary<string, (PromotedEvent Event, DateTimeOffset AtUtc)> _eventWrites = [];

    private void Wrote(PromotedEvent e)
    {
        _promotedEvents[e.Id] = e;
        _eventWrites[e.Id] = (e, DateTimeOffset.UtcNow);
    }

    /// <summary>Puts a plan on your Outlook calendar, or takes it off.</summary>
    public void SetPromotion(Guid id, bool onCalendar) =>
        Update(id, i => onCalendar
            ? i with { PromoteToCalendar = true, FireAtUtc = i.PlannedStartUtc?.AddMinutes(-_settings.PlanAlertLeadMinutes) ?? i.FireAtUtc }
            : i with { PromoteToCalendar = false, FireAtUtc = i.PlannedStartUtc ?? i.FireAtUtc });

    /// <summary>
    /// Brings each plan's Outlook event in line: creates, moves or removes rNotice's own events,
    /// follows a move you made in Outlook, and notes a deletion. One run at a time; a change
    /// made during a run triggers one more. Failures are logged and retried on the next change
    /// or calendar refresh.
    /// </summary>
    private async Task PromoteAsync()
    {
        if (_promoting) { _promoteAgain = true; return; }
        _promoting = true;
        try
        {
            do
            {
                _promoteAgain = false;
                await PromoteOnceAsync();
            } while (_promoteAgain);
        }
        finally
        {
            _promoting = false;
        }
    }

    private async Task PromoteOnceAsync()
    {
        // Until the calendar has been read, a missing event means nothing — don't call it lost.
        var known = _calendarKnownUtc is not null;
        (DateTimeOffset From, DateTimeOffset To) window = known ? _calendarWindow : (DateTimeOffset.MaxValue, DateTimeOffset.MaxValue);
        var steps = Promotion.Plan(_store.List(), _promotedEvents, window.From, window.To, _settings.PlanMinutes)
            .Where(s => known || s.Kind is not (PromotionKind.FollowOutlook or PromotionKind.Lost))
            .ToList();
        if (steps.Count == 0) return;

        var category = _settings.Rules.PromotedCategory;
        foreach (var step in steps)
        {
            var item = step.Item;
            try
            {
                switch (step.Kind)
                {
                    case PromotionKind.Create:
                    {
                        var body = (item.Snippet is { Length: > 0 } s ? s + "\n\n" : "") +
                                   $"Planned with rNotice. Item {item.Id}.";
                        var id = await _calendar.CreateEventAsync(item.Title, body, step.StartUtc!.Value, step.EndUtc!.Value,
                            category, reminderMinutesBeforeStart: 0, CancellationToken.None);
                        Wrote(new PromotedEvent(id, step.StartUtc.Value, step.EndUtc.Value));
                        Save(item.Id, i => i with { CalendarEventId = id, CalendarStartUtc = step.StartUtc, CalendarEndUtc = step.EndUtc });
                        Log.Info($"Calendar: added '{item.Title}' at {Display.Time(step.StartUtc.Value)}");
                        break;
                    }
                    case PromotionKind.Move:
                        await _calendar.MoveEventAsync(item.CalendarEventId!, step.StartUtc!.Value, step.EndUtc!.Value, CancellationToken.None);
                        Wrote(new PromotedEvent(item.CalendarEventId!, step.StartUtc.Value, step.EndUtc.Value));
                        Save(item.Id, i => i with { CalendarStartUtc = step.StartUtc, CalendarEndUtc = step.EndUtc });
                        Log.Info($"Calendar: moved '{item.Title}' to {Display.Time(step.StartUtc.Value)}");
                        break;

                    case PromotionKind.Remove:
                        try
                        {
                            await _calendar.DeleteOwnEventAsync(item.CalendarEventId!, category, CancellationToken.None);
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Not rNotice's (any more): leave the event alone, just forget it.
                            Log.Warn(ex.Message);
                        }
                        _promotedEvents.Remove(item.CalendarEventId!);
                        _eventWrites.Remove(item.CalendarEventId!);
                        Save(item.Id, i => i with { CalendarEventId = null, CalendarStartUtc = null, CalendarEndUtc = null });
                        Log.Info($"Calendar: removed '{item.Title}'");
                        break;

                    case PromotionKind.FollowOutlook:
                    {
                        var start = step.StartUtc!.Value;
                        var minutes = (int)Math.Round((step.EndUtc!.Value - start).TotalMinutes);
                        Save(item.Id, i => i with
                        {
                            PlannedStartUtc = start, PlannedMinutes = minutes,
                            // Working on it: the check-in moves with the end you dragged.
                            FireAtUtc = i.IsWorking ? step.EndUtc : start.AddMinutes(-_settings.PlanAlertLeadMinutes), LastFiredUtc = null,
                            CalendarStartUtc = start, CalendarEndUtc = step.EndUtc,
                            Note = $"Moved in Outlook to {Display.Time(start)}", NoteLink = null
                        });
                        Log.Info($"Calendar: '{item.Title}' was moved in Outlook; plan follows to {Display.Time(start)}");
                        break;
                    }
                    case PromotionKind.Lost:
                        Save(item.Id, i => i with
                        {
                            PromoteToCalendar = false, CalendarEventId = null, CalendarStartUtc = null, CalendarEndUtc = null,
                            Note = "Removed from your Outlook calendar; still planned here", NoteLink = null
                        });
                        Log.Info($"Calendar: '{item.Title}' was deleted in Outlook; still planned, no longer on the calendar");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Calendar: couldn't {step.Kind.ToString().ToLowerInvariant()} '{item.Title}'", ex);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Re-reads before saving: the awaits above give you time to change the item.</summary>
    private void Save(Guid id, Func<Item, Item> change)
    {
        if (_store.Get(id) is { } current) _store.Save(change(current), DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Opens a deep link from Graph. Only https: the link is data from a source, and data never
    /// gets to choose what program runs.
    /// </summary>
    public static void OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn("Refused to open a link that is not https");
            return;
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void RefreshNow()
    {
        _lastWorkbookCTag = null;
        _calendarPoll.Now();
        _workbookPoll.Now();
        _mailPoll.Now();
        _teamsPoll.Now();
        Tick();
    }

    public async void SignIn()
    {
        if (await _auth.SignInAsync()) RefreshNow();
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────────

    public bool HideFromCapture => CapturePolicy.Hide;

    /// <summary>
    /// Switches capture exclusion for every open rNotice window at once, and remembers it.
    /// </summary>
    public void SetHideFromCapture(bool hide)
    {
        CapturePolicy.Hide = hide;
        foreach (Window window in _app.Windows)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.SetCaptureExclusion(hwnd, hide);
        }

        var updated = _settings with { HideFromCapture = hide };
        if (updated.Save()) _settings = updated;
        Log.Info(hide ? "Windows hidden from screen capture" : "Windows visible to screen capture");
    }

    // ── Ignored senders ─────────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> IgnoredSenders => _settings.Rules.IgnoreSenders;

    /// <summary>Your own domain, which the menu never offers to ignore wholesale.</summary>
    public string? MyDomain => _me is { } me && me.Address.Contains('@') ? Senders.DomainEntry(me.Address) : null;

    /// <summary>
    /// Adds an address or <c>@domain</c> to the ignore list, saves it, and dismisses every mail
    /// item from that sender still in Intake — they'd have been skipped had the rule existed.
    /// Planned and done items are yours and stay.
    /// </summary>
    public void IgnoreSender(string entry)
    {
        if (!SaveIgnored(Senders.With(_settings.Rules.IgnoreSenders, entry))) return;

        var now = DateTimeOffset.UtcNow;
        var dismissed = 0;
        foreach (var item in _store.List(Stage.Intake).Where(i => i.Source == Source.Mail))
        {
            if (Senders.AddressOf(item.From) is { } address && Senders.Matches(address, [entry]))
            {
                _store.Save(item with { Stage = Stage.Dismissed }, now);
                dismissed++;
            }
        }

        Log.Info($"Ignoring {entry}; dismissed {dismissed} item(s) from it");
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the list from the editor. If anything was taken off, the inbox is re-read from
    /// the backfill window, so recent mail from that sender — skipped while ignored — comes
    /// back into the queue, quietly. Items dismissed by an ignore stay dismissed.
    /// </summary>
    public void SetIgnoredSenders(IReadOnlyList<string> entries)
    {
        var removed = _settings.Rules.IgnoreSenders
            .Where(old => !entries.Contains(old, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!SaveIgnored(entries)) return;

        Log.Info($"Ignored senders updated: {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}" +
                 (removed.Count > 0 ? $"; no longer ignoring {string.Join(", ", removed)}" : ""));

        if (removed.Count > 0 && _settings.MailEnabled)
        {
            _store.SetState(MailDeltaKey, null);
            RefreshNow();
        }
        Changed?.Invoke();
    }

    private bool SaveIgnored(IReadOnlyList<string> entries)
    {
        var updated = _settings with { Rules = _settings.Rules with { IgnoreSenders = entries } };
        if (!updated.Save())
        {
            _tray.Notify("Ignore list not saved", $"settings.json couldn't be read or written. See the log.");
            return false;
        }

        _settings = updated;
        // The rules are fixed per Intake, so the next poll needs a new one with the new list.
        if (_me is { } me) _sync = new SourceSync(_store, new Intake(_settings.Rules, me));
        return true;
    }

    private IgnoredSendersWindow? _ignoredWindow;

    public void OpenIgnoredSenders()
    {
        if (_ignoredWindow is { IsLoaded: true })
        {
            _ignoredWindow.Activate();
            return;
        }
        _ignoredWindow = new IgnoredSendersWindow(this);
        _ignoredWindow.Closed += (_, _) => _ignoredWindow = null;
        _ignoredWindow.Show();
    }

    public void OpenQueue()
    {
        _queue ??= CreateQueue();
        _queue.Show();
        if (_queue.WindowState == WindowState.Minimized) _queue.WindowState = WindowState.Normal;
        _queue.Activate();
    }

    private QueueWindow CreateQueue()
    {
        var window = new QueueWindow(this);
        Changed += window.Reload;
        return window;
    }

    /// <summary>
    /// Shows the alert for the next upcoming meeting, as it will look at T−1, without marking
    /// anything fired. For checking the alert's look while it's still being iterated on.
    /// </summary>
    public void PreviewMeetingAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var next = _store.List(Stage.Planned)
            .Where(i => i.Source == Source.Calendar && i.DueUtc > now)
            .OrderBy(i => i.DueUtc)
            .FirstOrDefault();

        if (next is null)
        {
            _tray.Notify("No meeting to preview", "There are no upcoming meetings in the queue.");
            return;
        }
        ShowAlert([next], takeover: true);
    }

    /// <summary>
    /// Shows the alert. For a takeover, the dim goes up first on every monitor and the alert
    /// sits on top of it; the dim comes down when the alert closes or when you double-click it.
    /// </summary>
    private void ShowAlert(IReadOnlyList<Item> items, bool takeover)
    {
        if (takeover) ShowDim();

        if (_alert is { IsLoaded: true })
        {
            _alert.Add(items, takeover);
            if (takeover) RaiseAboveDim(_alert);
            return;
        }

        _alert = new AlertWindow(this, items, takeover ? ArmingDelay : TimeSpan.Zero);
        Changed += _alert.Reload;
        _alert.Closed += (_, _) => { Changed -= _alert.Reload; _alert = null; ClearDim(); };
        _alert.Show();
    }

    private TimeSpan ArmingDelay => TimeSpan.FromMilliseconds(Math.Clamp(_settings.ArmingDelayMs, 0, 5000));
    private readonly List<TakeoverOverlay> _dim = [];

    private void ShowDim()
    {
        if (_dim.Count > 0) return;

        // The hint goes on the monitor with the mouse, which is where the alert appears.
        var here = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var overlay = new TakeoverOverlay(screen, _settings.TakeoverDim, showHint: screen.Equals(here), ArmingDelay, ClearDim);
            overlay.Show();
            _dim.Add(overlay);
        }
    }

    /// <summary>Takes the dim down. The alert, if it's up, stays.</summary>
    public void ClearDim()
    {
        if (_dim.Count == 0) return;
        foreach (var overlay in _dim) overlay.Close();
        _dim.Clear();
    }

    /// <summary>Re-asserting Topmost puts a window at the top of the topmost band, above the dim.</summary>
    private static void RaiseAboveDim(Window window)
    {
        window.Topmost = false;
        window.Topmost = true;
    }

    /// <summary>The broker parents its sign-in dialog to a real window, or it can open behind everything.</summary>
    private IntPtr ParentWindowHandle()
    {
        _queue ??= CreateQueue();
        return new WindowInteropHelper(_queue).EnsureHandle();
    }

    public void Exit()
    {
        _timer?.Stop();
        _app.Shutdown();
    }

    public void Dispose()
    {
        _timer?.Stop();
        _hotKey?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
    }

    /// <summary>When a source next polls, and backoff after failures: doubling, capped.</summary>
    private sealed class PollState(string name)
    {
        private int _failures;
        private DateTimeOffset _next = DateTimeOffset.MinValue;

        public string Name { get; } = name;
        public bool Running { get; set; }
        public DateTimeOffset Next => _next;
        public SourceStatus Status { get; private set; } = new(name, null, "Not polled yet", false);

        public bool IsDue(DateTimeOffset now) => !Running && now >= _next;
        public void Now() => _next = DateTimeOffset.MinValue;

        public void Succeeded(DateTimeOffset now, string summary, TimeSpan interval)
        {
            _failures = 0;
            _next = now + interval;
            Status = new SourceStatus(Name, now, summary, false);
        }

        public TimeSpan Failed(DateTimeOffset now, string why, TimeSpan interval, bool backoff, TimeSpan? cap = null)
        {
            _failures++;
            var wait = backoff
                ? TimeSpan.FromTicks(Math.Min(cap!.Value.Ticks, interval.Ticks * (1L << Math.Min(_failures - 1, 10))))
                : interval;
            _next = now + wait;
            Status = Status with { Summary = why, Failing = true };
            return wait;
        }
    }
}
