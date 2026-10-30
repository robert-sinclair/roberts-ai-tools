using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using rNotice.Core.Rules;

namespace rNotice.Agent;

/// <summary>
/// A holding area for new things until they have a proper place: every timer's countdown, and
/// Teams presence live while a meeting is under way. Read-only apart from Refresh and Check now.
/// </summary>
public partial class DevViewWindow : Window
{
    private readonly AgentHost _host;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    internal DevViewWindow(AgentHost host)
    {
        _host = host;
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _clock.Tick += (_, _) => Render();
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
        RoutineUrl.Text = _host.RoutineFireUrl ?? "";
        Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        RenderTimers(now);
        RenderPresence(now);
        RenderRoutine();
    }

    private void RenderTimers(DateTimeOffset now)
    {
        var timers = _host.DevTimers();
        Timers.Children.Clear();
        Timers.RowDefinitions.Clear();
        for (var row = 0; row < timers.Count; row++)
        {
            var t = timers[row];
            Timers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(row, 0, t.Name, bold: true);
            Add(row, 1, t.Running ? "running…" : Countdown(t.NextUtc, now), mono: true);
            Add(row, 2, t.Detail, failing: t.Failing);
        }
    }

    private void Add(int row, int column, string text, bool bold = false, bool mono = false, bool failing = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 8, 2),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = mono ? new FontFamily("Consolas") : FontFamily,
            Foreground = failing ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)) : Foreground
        };
        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        Timers.Children.Add(block);
    }

    private static string Countdown(DateTimeOffset? next, DateTimeOffset now)
    {
        if (next is not { } t) return "—";
        var left = t - now;
        if (left <= TimeSpan.Zero) return "due";
        return left.TotalHours >= 1 ? $"in {(int)left.TotalHours}h {left.Minutes:00}m"
            : left.TotalMinutes >= 1 ? $"in {(int)left.TotalMinutes}m {left.Seconds:00}s"
            : $"in {left.Seconds}s";
    }

    private void RenderPresence(DateTimeOffset now)
    {
        var p = _host.DevPresenceNow();
        if (!p.Enabled)
        {
            PresenceMeetings.Text = "Off: presenceEnabled is false in settings.";
            PresenceValue.Text = "";
            PresenceDetail.Text = "";
            return;
        }

        PresenceMeetings.Text = p.MeetingsUnderWay.Count == 0
            ? "No meeting under way, so presence isn't being read."
            : "Under way: " + string.Join("; ", p.MeetingsUnderWay);

        if (p.MeetingsUnderWay.Count == 0 || p.Activity is null)
        {
            PresenceValue.Text = p.MeetingsUnderWay.Count == 0 ? "" : p.Running ? "Reading…" : "Not read yet";
            PresenceDetail.Text = p.MeetingsUnderWay.Count == 0 ? "" : $"Next read {Countdown(p.NextUtc, now)}";
            return;
        }

        PresenceValue.Text = p.Activity;
        PresenceDetail.Text =
            (Presence.IsInACall(p.Activity) ? "Counts as in a call: a meeting under way is marked joined." : "Not a call: nothing is marked joined.") +
            $"  Read {(int)(now - p.CheckedUtc!.Value).TotalSeconds}s ago · next {Countdown(p.NextUtc, now)}.";
    }

    private void RenderRoutine()
    {
        var r = _host.DevRoutineNow();
        RoutineStatus.Text = r.Configured
            ? $"Set up. {r.Waiting} waiting, {r.Sent} out, {r.FiresLastHour} sent in the last hour."
            : "Not set up: paste the fire URL and token below.";
        RoutineLast.Text = $"{r.Last}\nCase map: {r.CasesKnown} case(s) known. {r.CaseLookup}.";
    }

    private void OnSaveRoutine(object sender, RoutedEventArgs e)
    {
        var error = _host.SetRoutine(RoutineUrl.Text, RoutineToken.Password);
        RoutineResult.Text = error ?? "Saved.";
        if (error is null) RoutineToken.Clear();
    }

    private void OnClearRoutine(object sender, RoutedEventArgs e)
    {
        _host.ClearRoutine();
        RoutineUrl.Text = "";
        RoutineToken.Clear();
        RoutineResult.Text = "Removed.";
    }

    private void OnRefreshNow(object sender, RoutedEventArgs e) => _host.RefreshNow();

    private async void OnCheckPresence(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckResult.Text = "Reading…";
        CheckResult.Text = $"{DateTime.Now:HH:mm:ss}  {await _host.CheckPresenceNowAsync()}";
        CheckButton.IsEnabled = true;
    }
}
