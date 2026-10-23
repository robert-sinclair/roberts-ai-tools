using System.Diagnostics;
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
    private readonly HashSet<string> _loggedSkips = [];

    public event Action? Changed;

    public IReadOnlyList<SourceStatus> Statuses =>
        _settings.MailEnabled ? [_calendarPoll.Status, _workbookPoll.Status, _mailPoll.Status] : [_calendarPoll.Status, _workbookPoll.Status];

    /// <summary>True while either source is mid-poll.</summary>
    public bool Refreshing => _calendarPoll.Running || _workbookPoll.Running || _mailPoll.Running;

    /// <summary>
    /// When the next poll starts — the sooner of the two sources. Later than a minute away only
    /// when a source is backing off after failures, which is exactly when it's worth seeing.
    /// </summary>
    public DateTimeOffset NextRefreshUtc =>
        new[] { _calendarPoll, _workbookPoll, _mailPoll }
            .Where(p => p != _mailPoll || _settings.MailEnabled)
            .Min(p => p.Next);
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

        if (_calendarPoll.IsDue(now)) _ = PollAsync(_calendarPoll, PollCalendarAsync, _settings.RefreshInterval);
        if (_workbookPoll.IsDue(now)) _ = PollAsync(_workbookPoll, PollWorkbookAsync, _settings.RefreshInterval);
        if (_settings.MailEnabled && _mailPoll.IsDue(now)) _ = PollAsync(_mailPoll, PollMailAsync, _settings.RefreshInterval);

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

    private async Task<string> PollCalendarAsync(DateTimeOffset now)
    {
        var events = await _calendar.GetViewAsync(now.AddHours(-1), now.AddDays(_settings.Rules.HorizonDays), CancellationToken.None);

        _busy = events.Where(e => !e.IsCancelled && !e.IsAllDay)
                      .Select(e => new BusyBlock(e.StartUtc, e.EndUtc, e.ShowAs))
                      .ToList();

        // For planning, anything not marked free takes the time: busy, out of office,
        // tentative, working elsewhere — all-day ones included, so a day off isn't offered.
        _meetingEvents = events.Where(e => !e.IsCancelled && e.ShowAs != ShowAs.Free).ToList();
        _meetingBlocks = _meetingEvents.Select(e => new TimeBlock(e.StartUtc, e.EndUtc)).ToList();
        _calendarKnownUtc = now;

        var report = _sync!.SyncCalendar(events, now);
        LogSync("Calendar", report);
        MoveConflictsToIntake(now);
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
    private const string ReadStateBackfillKey = "mail.readState.backfilled";

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

    public void SetStage(Guid id, Stage stage) => Update(id, i => i with { Stage = stage });

    /// <summary>
    /// Asks "when will you do it?" and plans the item. Every way of planning comes through here —
    /// P, the right-click menu, a drag from Intake, the alert's Plan button — so they all behave
    /// the same. Cancel changes nothing.
    /// </summary>
    public void Plan(Guid id, Window? owner)
    {
        if (_store.Get(id) is not { } item) return;

        var hours = _settings.ToWorkHours();
        var dialog = new PlanDialog(item, _settings.PlanMinutes,
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

        Update(id, i => i.Plan(dialog.StartUtc, dialog.StartUtc is null ? null : dialog.Minutes));
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
        var meetings = _meetingEvents.Select(e => new AgendaEntry(e.StartUtc, e.EndUtc, e.Subject, IsMeeting: true));
        var zone = _settings.ToWorkHours().Zone;
        foreach (var (task, meeting) in PlanConflicts.Find(_store.List(Stage.Planned), meetings, now, _settings.PlanMinutes))
        {
            var note = PlanConflicts.Note(task, meeting, zone, _settings.PlanMinutes);
            _store.Save(task.BackToIntake(note, now), now);
            Log.Info($"'{task.Title}' moved back to Intake: clashes with '{meeting.Title}'");
            _tray.Notify("Moved back to Intake", $"{task.Title} — its slot now clashes with {meeting.Title}.");
        }
    }

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
            var item = _store.GetBySource(Source.Calendar, e.Id) is { IsOpen: true } open ? open : null;
            entries.Add(new CalendarEntry(item, e.Subject, e.StartUtc, e.EndUtc,
                item is null ? CalendarKind.Busy : CalendarKind.Meeting, e.IsAllDay));
        }

        foreach (var task in _store.List(Stage.Planned).Where(i => i.Source != Source.Calendar &&
                     i.PlannedStartUtc is { } s && s < to && s.AddMinutes(i.PlannedMinutes ?? _settings.PlanMinutes) > from))
        {
            var start = task.PlannedStartUtc!.Value;
            entries.Add(new CalendarEntry(task, task.Title, start, start.AddMinutes(task.PlannedMinutes ?? _settings.PlanMinutes),
                CalendarKind.Task, false));
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
        Update(id, i => i.Plan(startUtc, _settings.PlanMinutes));
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

    /// <summary>Opens the Teams call and notes that you joined through rNotice.</summary>
    public void Join(Guid id)
    {
        if (_store.Get(id) is not { JoinUrl: { } url }) return;
        OpenLink(url);
        Update(id, i => i with { JoinedUtc = DateTimeOffset.UtcNow });
    }

    public void Snooze(Guid id) =>
        Update(id, i => i.Snooze(DateTimeOffset.UtcNow.AddMinutes(_settings.SnoozeMinutes)));

    private void Update(Guid id, Func<Item, Item> change)
    {
        if (_store.Get(id) is not { } item) return;
        var updated = change(item);
        _store.Save(updated, DateTimeOffset.UtcNow);
        Log.Info($"{updated.Source} item '{updated.Title}' → {updated.Stage}" +
                 (updated.SnoozedUntilUtc != item.SnoozedUntilUtc ? $", snoozed {_settings.SnoozeMinutes} min" : ""));
        Changed?.Invoke();
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
