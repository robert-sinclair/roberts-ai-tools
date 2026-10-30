using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using rNotice.Core.Items;
using rNotice.Core.Scheduling;
using WinForms = System.Windows.Forms;

namespace rNotice.Agent;

/// <summary>
/// The topmost alert. Shown without taking focus — your typing stays where it was — and the
/// taskbar flashes instead. Several items arriving together share one window as a list.
///
/// A meeting gets the large form: most of the monitor's width, the title in big type, a
/// countdown ticking every second, and Join first. It's the step before step 4's full-screen
/// takeover, which still waits on the Teams capture check.
/// </summary>
public partial class AlertWindow : Window
{
    private readonly AgentHost _host;
    private readonly Dictionary<Guid, Stage> _shown = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<ItemView> _views = [];
    private int _ticks;

    internal AlertWindow(AgentHost host, IReadOnlyList<Item> items, TimeSpan arming)
    {
        _host = host;
        InitializeComponent();
        foreach (var item in items) _shown[item.Id] = item.Stage;
        Arm(arming);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromCapture(hwnd);
            NativeMethods.Flash(hwnd);
        };
        Loaded += (_, _) => Place();
        SizeChanged += (_, _) => { if (IsLoaded) Centre(); };
        _clock.Tick += (_, _) => OnClock();
        _clock.Start();
        Closed += (_, _) => _clock.Stop();

        Reload();
    }

    private bool HasMeeting => _views.Any(v => v.IsMeeting);

    /// <summary>
    /// Ignores clicks for a moment after a takeover appears. A click already travelling toward
    /// whatever you were doing shouldn't land on Mark done in a window you never saw.
    /// </summary>
    private void Arm(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        IsHitTestVisible = false;
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => { IsHitTestVisible = true; timer.Stop(); };
        timer.Start();
    }

    public void Add(IReadOnlyList<Item> items, bool takeover)
    {
        var wasMeeting = HasMeeting;
        foreach (var item in items) _shown[item.Id] = item.Stage;
        if (takeover) Arm(TimeSpan.FromMilliseconds(750));
        Reload();
        if (HasMeeting != wasMeeting) Place();
        NativeMethods.Flash(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Countdowns update in place every second. The item list itself is re-read every 15
    /// seconds, and rebuilt only when it actually changed, so a button never disappears under
    /// a click that's in progress.
    /// </summary>
    private void OnClock()
    {
        foreach (var view in _views) view.Tick();
        if (++_ticks % 15 == 0) Reload();
    }

    /// <summary>
    /// Re-reads each item. An item drops off once its stage differs from when it was shown, or
    /// it's snoozed — whether that happened here or in the queue window. When nothing is left
    /// the window closes. Compared to the shown stage rather than to Intake because meetings
    /// arrive already Planned.
    /// </summary>
    public void Reload()
    {
        var now = DateTimeOffset.UtcNow;
        var live = _host.Items()
            .Where(i => _shown.TryGetValue(i.Id, out var stage) && i.Stage == stage && !(i.SnoozedUntilUtc > now))
            .OrderBy(i => i.Source == Source.Calendar ? 0 : 1)
            .ThenBy(i => i.Classification.Priority).ThenBy(i => i.DueUtc)
            .ToList();

        if (live.Count == 0)
        {
            Close();
            return;
        }

        if (!live.Select(i => i.Id).SequenceEqual(_views.Select(v => v.Id)) ||
            live.Any(i => _views.First(v => v.Id == i.Id) is var v && (v.Item != i || v.NextUp?.Id != _host.NextUp(i.Id)?.Id)))
        {
            _views = live.Select(i => new ItemView(i)
            {
                NextUp = _host.NextUp(i.Id),
                RebookMinutes = _host.MeetingRebookMinutes(i.Id)
            }).ToList();
            ItemsList.ItemsSource = _views;
        }

        var meetings = live.Count(i => i.Source == Source.Calendar);
        Heading.Text = meetings > 0
            ? (meetings == 1 ? "Meeting" : $"{meetings} meetings") + (live.Count > meetings ? $" and {live.Count - meetings} more" : "")
            : live.Count == 1 ? "rNotice" : $"rNotice — {live.Count} items";
    }

    /// <summary>
    /// On the monitor with the mouse. A meeting takes most of its width and sits centred; anything
    /// else is a compact panel a third of the way down.
    /// </summary>
    private void Place()
    {
        var (_, screenWidth, screenHeight) = MouseScreen();

        if (HasMeeting)
        {
            // A window sizing itself to its content ignores a new Width. Switch that off, set
            // the width, and switch it back on so the height still follows the content.
            SizeToContent = SizeToContent.Manual;
            Width = Math.Clamp(screenWidth * 0.6, 760, 1300);
            SizeToContent = SizeToContent.Height;

            Root.Margin = new Thickness(40, 28, 40, 28);
            Heading.FontSize = 22;
            BorderThickness = new Thickness(4);
        }
        MaxHeight = screenHeight * 0.9;
        Centre();
    }

    /// <summary>Runs on every real size change too, so the window stays centred as it grows.</summary>
    private void Centre()
    {
        var (screen, screenWidth, screenHeight) = MouseScreen();
        Left = screen.X + (screenWidth - ActualWidth) / 2;
        Top = screen.Y + (screenHeight - ActualHeight) / (HasMeeting ? 2 : 3);
    }

    /// <summary>Working area of the monitor with the mouse, in this window's units.</summary>
    private (Point TopLeft, double Width, double Height) MouseScreen()
    {
        var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        return (new Point(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY),
                area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    private static Guid IdOf(object sender) => (Guid)((FrameworkElement)sender).Tag;
    private Item? ItemOf(object sender) => _host.Items().FirstOrDefault(i => i.Id == IdOf(sender));

    private void OnPlan(object sender, RoutedEventArgs e) => _host.Plan(IdOf(sender), this);
    private void OnDone(object sender, RoutedEventArgs e) => _host.SetStage(IdOf(sender), Stage.Done);
    // ── Working on it ───────────────────────────────────────────────────────────────────────

    private void OnStartWork(object sender, RoutedEventArgs e)
    {
        _host.StartWork(IdOf(sender));
        _shown.Remove(IdOf(sender));
        Reload();
    }

    private void OnKeepLoaded(object sender, RoutedEventArgs e)
    {
        var box = (ComboBox)sender;
        box.ItemsSource = _host.KeepGoingChoices((Guid)box.Tag).Select(c => c.Text).ToList();
        box.SelectedIndex = 0;
    }

    private void OnKeepWorking(object sender, RoutedEventArgs e)
    {
        var id = IdOf(sender);
        var box = ((FrameworkElement)sender).Parent is Panel panel ? panel.Children.OfType<ComboBox>().FirstOrDefault() : null;
        _host.KeepWorking(id, AgentHost.KeepGoingMinutes[Math.Max(0, box?.SelectedIndex ?? 0)]);
        _shown.Remove(id);
        Reload();
    }

    private void OnStartNext(object sender, RoutedEventArgs e)
    {
        var id = IdOf(sender);
        if (_host.NextUp(id) is not { } next) return;
        _host.SwitchTo(id, next.Id);
        _shown.Remove(id);
        _shown.Remove(next.Id);
        Reload();
    }

    // ── Rebook in ───────────────────────────────────────────────────────────────────────────

    private sealed record RebookOption(RebookChoice Choice, string Text)
    {
        public override string ToString() => Text;
    }

    private void OnRebookLoaded(object sender, RoutedEventArgs e) => FillRebook((ComboBox)sender);

    /// <summary>Fresh choices every time it opens: the calendar may have changed since.</summary>
    private void OnRebookOpened(object? sender, EventArgs e) => FillRebook((ComboBox)sender!);

    /// <summary>
    /// Refills the choices, keeping the one you'd picked if it's still on offer; otherwise the
    /// first — next free, 30 min — so Book works in one click.
    /// </summary>
    private void FillRebook(ComboBox box)
    {
        var kept = (box.SelectedItem as RebookOption)?.Choice;
        var options = _host.RebookChoices((Guid)box.Tag).Select(c => new RebookOption(c, Describe(c))).ToList();
        box.ItemsSource = options;
        box.SelectedIndex = Math.Max(0, options.FindIndex(o => o.Choice == kept));
        box.IsEnabled = options.Count > 0;
    }

    /// <summary>Books what the dropdown shows. Choosing in the dropdown alone books nothing.</summary>
    private void OnRebookBook(object sender, RoutedEventArgs e)
    {
        var id = IdOf(sender);
        var box = ((FrameworkElement)sender).Parent is Panel panel ? panel.Children.OfType<ComboBox>().FirstOrDefault() : null;
        if (box?.SelectedItem is not RebookOption option) return;
        _host.RebookAt(id, option.Choice);
        _shown.Remove(id);
        Reload();
    }

    internal static string Describe(RebookChoice c)
    {
        var start = c.StartUtc.ToLocalTime();
        var end = start.AddMinutes(c.Minutes);
        var when = start.Date == DateTime.Today ? $"{start:HH:mm}–{end:HH:mm}" : $"{start:ddd} {start:HH:mm}–{end:HH:mm}";
        var kind = c.Kind switch
        {
            RebookKind.NextFree => "Next free",
            RebookKind.HourLater => "1 h+ later",
            _ => "Tomorrow"
        };
        return $"{kind} · {when} ({(c.Minutes == 60 ? "1 h" : $"{c.Minutes} min")})";
    }

    // ── Moving a meeting, and following one up ──────────────────────────────────────────────

    private void OnMeetingRebookLoaded(object sender, RoutedEventArgs e) => FillMeetingRebook((ComboBox)sender);

    /// <summary>Fresh choices every time it opens: the calendar may have changed since.</summary>
    private void OnMeetingRebookOpened(object? sender, EventArgs e) => FillMeetingRebook((ComboBox)sender!);

    private void FillMeetingRebook(ComboBox box)
    {
        var kept = (box.SelectedItem as RebookOption)?.Choice;
        var options = _host.MeetingRebookChoices((Guid)box.Tag).Select(c => new RebookOption(c, Describe(c))).ToList();
        box.ItemsSource = options;
        box.SelectedIndex = Math.Max(0, options.FindIndex(o => o.Choice == kept));
        box.IsEnabled = options.Count > 0;
    }

    /// <summary>
    /// Moves the meeting to what the dropdown shows — in Outlook, so everyone invited is told.
    /// Choosing in the dropdown alone moves nothing.
    /// </summary>
    private void OnMeetingRebook(object sender, RoutedEventArgs e)
    {
        var id = IdOf(sender);
        var box = ((FrameworkElement)sender).Parent is Panel panel ? panel.Children.OfType<ComboBox>().FirstOrDefault() : null;
        if (box?.SelectedItem is not RebookOption option) return;
        _host.RebookMeeting(id, option.Choice);
        _shown.Remove(id);
        Reload();
    }

    /// <summary>The meeting stays on this window: following it up doesn't deal with it.</summary>
    private void OnFollowUp(object sender, RoutedEventArgs e) => _host.FollowUp(IdOf(sender), this);

    private void OnOpen(object sender, RoutedEventArgs e) => AgentHost.OpenLink(ItemOf(sender)?.SourceRef);

    /// <summary>
    /// Opens the call, notes it as joined, and takes the meeting off this window. It doesn't mark the meeting done:
    /// the item goes by itself when the meeting ends, and a Done meeting that's later moved used
    /// to miss its alert at the new time.
    /// </summary>
    private void OnJoin(object sender, RoutedEventArgs e)
    {
        _host.Join(IdOf(sender));
        _shown.Remove(IdOf(sender));
        Reload();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Meetings get the large template; everything else the compact one.</summary>
public sealed class AlertTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Meeting { get; set; }
    public DataTemplate? Task { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ItemView { IsMeeting: true } ? Meeting : Task;
}
