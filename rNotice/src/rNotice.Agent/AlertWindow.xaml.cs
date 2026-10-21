using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using rNotice.Core.Items;
using WinForms = System.Windows.Forms;

namespace rNotice.Agent;

/// <summary>
/// The topmost alert. Shown without taking focus — your typing stays where it was — and the
/// taskbar flashes instead. Several items arriving together share one window as a list.
///
/// Until step 4 adds the takeover, meetings arrive here too, headed "Meeting starting".
/// </summary>
public partial class AlertWindow : Window
{
    private readonly AgentHost _host;
    private readonly List<Guid> _ids = [];
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    internal AlertWindow(AgentHost host, IReadOnlyList<Item> items)
    {
        _host = host;
        InitializeComponent();
        _ids.AddRange(items.Select(i => i.Id));

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromCapture(hwnd);
            NativeMethods.Flash(hwnd);
        };
        Loaded += (_, _) => CentreOnMouseMonitor();
        _clock.Tick += (_, _) => Reload();
        _clock.Start();
        Closed += (_, _) => _clock.Stop();

        Reload();
    }

    public void Add(IReadOnlyList<Item> items)
    {
        foreach (var item in items)
            if (!_ids.Contains(item.Id)) _ids.Add(item.Id);
        Reload();
        NativeMethods.Flash(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Re-reads each item. Anything planned, dismissed, completed or snoozed — here or from the
    /// queue window — drops off; when nothing is left the window closes.
    /// </summary>
    public void Reload()
    {
        var now = DateTimeOffset.UtcNow;
        var live = _host.Items()
            .Where(i => _ids.Contains(i.Id) && i.Stage == Stage.Intake && !(i.SnoozedUntilUtc > now))
            .OrderBy(i => i.Classification.Priority).ThenBy(i => i.DueUtc)
            .ToList();

        if (live.Count == 0)
        {
            Close();
            return;
        }

        ItemsList.ItemsSource = live.Select(i => new ItemView(i)).ToList();
        Heading.Text = live.Any(i => i.Classification.Interrupt == Interrupt.Takeover && i.DueUtc > now.AddMinutes(-5))
            ? "Meeting starting"
            : live.Count == 1 ? "rNotice" : $"rNotice — {live.Count} items";
    }

    private void CentreOnMouseMonitor()
    {
        var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = ActualWidth;
        var height = ActualHeight;
        Left = screen.Left / dpi.DpiScaleX + (screen.Width / dpi.DpiScaleX - width) / 2;
        Top = screen.Top / dpi.DpiScaleY + (screen.Height / dpi.DpiScaleY - height) / 3;
    }

    private static Guid IdOf(object sender) => (Guid)((FrameworkElement)sender).Tag;
    private Item? ItemOf(object sender) => _host.Items().FirstOrDefault(i => i.Id == IdOf(sender));

    private void OnPlan(object sender, RoutedEventArgs e) => _host.SetStage(IdOf(sender), Stage.Planned);
    private void OnDismiss(object sender, RoutedEventArgs e) => _host.SetStage(IdOf(sender), Stage.Dismissed);
    private void OnSnooze(object sender, RoutedEventArgs e) => _host.Snooze(IdOf(sender));
    private void OnOpen(object sender, RoutedEventArgs e) => AgentHost.OpenLink(ItemOf(sender)?.SourceRef);

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        AgentHost.OpenLink(ItemOf(sender)?.JoinUrl);
        _host.SetStage(IdOf(sender), Stage.Done);
    }

    private void OnSnoozeLoaded(object sender, RoutedEventArgs e) =>
        ((Button)sender).Content = $"Snooze {_host.SnoozeMinutes} min";

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
