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
    private ItemStore _store = null!;
    private Scheduler _scheduler = null!;
    private SourceSync? _sync;
    private Task? _meLookup;
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
    private readonly HashSet<string> _loggedSkips = [];

    public event Action? Changed;

    public IReadOnlyList<SourceStatus> Statuses => [_calendarPoll.Status, _workbookPoll.Status];

    /// <summary>True while either source is mid-poll.</summary>
    public bool Refreshing => _calendarPoll.Running || _workbookPoll.Running;

    /// <summary>
    /// When the next poll starts — the sooner of the two sources. Later than a minute away only
    /// when a source is backing off after failures, which is exactly when it's worth seeing.
    /// </summary>
    public DateTimeOffset NextRefreshUtc =>
        _calendarPoll.Next < _workbookPoll.Next ? _calendarPoll.Next : _workbookPoll.Next;
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

        _auth = new AgentAuth(ParentWindowHandle);
        _auth.StateChanged += () => Changed?.Invoke();
        _graph = new GraphClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, _auth.GetTokenAsync);
        _calendar = new CalendarClient(_graph);
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
            Log.Info($"Alert: {string.Join("; ", windowed.Select(i => i.Title))}");
            ShowAlert(windowed);
        }

        foreach (var toast in result.Toasts)
            _tray.Notify(toast.Title, Display.When(toast));

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

        var report = _sync!.SyncCalendar(events, now);
        LogSync("Calendar", report);
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

        var dialog = new PlanDialog(item);
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

        Update(id, i => i.Plan(dialog.StartUtc));
        if (dialog.StartUtc is { } start)
            Log.Info($"'{item.Title}' planned for {Display.Time(start)}");
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
        Tick();
    }

    public async void SignIn()
    {
        if (await _auth.SignInAsync()) RefreshNow();
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────────

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

    private void ShowAlert(IReadOnlyList<Item> items)
    {
        if (_alert is { IsLoaded: true })
        {
            _alert.Add(items);
            return;
        }

        _alert = new AlertWindow(this, items);
        Changed += _alert.Reload;
        _alert.Closed += (_, _) => { Changed -= _alert.Reload; _alert = null; };
        _alert.Show();
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
