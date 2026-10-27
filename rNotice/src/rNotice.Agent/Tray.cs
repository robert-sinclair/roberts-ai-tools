using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using rNotice.Core.Items;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace rNotice.Agent;

internal enum TrayState
{
    /// <summary>Polling fine, nothing urgent waiting.</summary>
    Normal,

    /// <summary>P1 or P2 waiting in Intake.</summary>
    Attention,

    /// <summary>Sign-in needed or a source failing. The queue may be stale.</summary>
    Warning
}

/// <summary>
/// The notification-area icon and its menu. Icons are drawn at runtime, as in rMic, so they
/// stay in sync and scale cleanly.
/// </summary>
internal sealed class Tray : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly AgentHost _host;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _signInItem;
    private readonly WinForms.ToolStripMenuItem _startupItem;
    private readonly WinForms.ToolStripMenuItem _captureItem;
    private readonly Dictionary<TrayState, Drawing.Icon> _icons = [];
    private readonly List<IntPtr> _handles = [];

    public Tray(AgentHost host)
    {
        _host = host;
        foreach (var state in Enum.GetValues<TrayState>()) _icons[state] = Build(state, 32);

        _statusItem = new WinForms.ToolStripMenuItem("Starting…") { Enabled = false };
        _signInItem = new WinForms.ToolStripMenuItem("Sign in…", null, (_, _) => _host.SignIn()) { Visible = false };
        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows", null, OnStartupClicked)
        {
            Checked = AutoStart.IsEnabled()
        };

        // Ticked by default. Untick to take a screenshot or share the alert in a call.
        _captureItem = new WinForms.ToolStripMenuItem("Hide from screen capture", null, (_, _) =>
        {
            _host.SetHideFromCapture(!_host.HideFromCapture);
            _captureItem!.Checked = _host.HideFromCapture;
        })
        { Checked = _host.HideFromCapture };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            _statusItem,
            _signInItem,
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem($"Open queue ({HotKeyWindow.Description})", null, (_, _) => _host.OpenQueue())
                { Font = new Drawing.Font(WinForms.Control.DefaultFont, Drawing.FontStyle.Bold) },
            new WinForms.ToolStripMenuItem("Refresh now", null, (_, _) => _host.RefreshNow()),
            new WinForms.ToolStripMenuItem("Preview meeting alert", null, (_, _) => _host.PreviewMeetingAlert()),
            new WinForms.ToolStripMenuItem("Dev view", null, (_, _) => _host.OpenDevView()),
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Ignored senders…", null, (_, _) => _host.OpenIgnoredSenders()),
            new WinForms.ToolStripMenuItem("Watched Teams chats…", null, (_, _) => _host.OpenWatchedChats()),
            _captureItem,
            _startupItem,
            new WinForms.ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(Paths.Logs)),
            new WinForms.ToolStripMenuItem("Open settings file", null, (_, _) => OpenFolder(Paths.Root)),
            new WinForms.ToolStripSeparator(),
            new WinForms.ToolStripMenuItem("Exit rNotice", null, (_, _) => _host.Exit())
        ]);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _icons[TrayState.Normal],
            Text = "rNotice",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _host.OpenQueue();
        _icon.BalloonTipClicked += (_, _) => _host.OpenQueue();
    }

    /// <summary>A toast. On Windows 10 and 11 a balloon is shown as a notification.</summary>
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, Truncate(title, 60), Truncate(text, 200), WinForms.ToolTipIcon.Info);

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var open = _host.Items().Where(i => i.Stage == Stage.Intake).ToList();
        var urgent = open.Count(i => QueueOrder.NeedsAttention(i, now));
        var failing = _host.Statuses.Where(s => s.Failing).ToList();

        var state = _host.NeedsSignIn || failing.Count > 0 ? TrayState.Warning
            : urgent > 0 ? TrayState.Attention
            : TrayState.Normal;

        _icon.Icon = _icons[state];
        _signInItem.Visible = _host.NeedsSignIn;

        var status = _host.NeedsSignIn ? "Sign-in needed — click Sign in"
            : failing.Count > 0 ? $"{failing[0].Name}: {failing[0].Summary}"
            : $"{open.Count} in intake, {urgent} urgent";
        _statusItem.Text = status;
        _icon.Text = Truncate($"rNotice — {status}", 127);
    }

    private void OnStartupClicked(object? sender, EventArgs e)
    {
        var want = !_startupItem.Checked;
        if (AutoStart.Set(want)) _startupItem.Checked = want;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>A rounded square with a bell-like dot, coloured by state.</summary>
    private Drawing.Icon Build(TrayState state, int size)
    {
        var color = state switch
        {
            TrayState.Attention => Drawing.Color.FromArgb(0xE0, 0x8B, 0x1E),
            TrayState.Warning => Drawing.Color.FromArgb(0x8A, 0x8A, 0x8A),
            _ => Drawing.Color.FromArgb(0x2B, 0x6C, 0xB0)
        };

        using var bitmap = new Drawing.Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Drawing.Color.Transparent);

            var r = size / 5f;
            using var path = new GraphicsPath();
            var rect = new Drawing.RectangleF(1, 1, size - 2, size - 2);
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            using (var fill = new Drawing.SolidBrush(color)) g.FillPath(fill, path);

            var glyph = state == TrayState.Warning ? "!" : "N";
            using var font = new Drawing.Font("Segoe UI", size * 0.55f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString(glyph, font, white, new Drawing.RectangleF(0, 1, size, size), format);
        }

        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Drawing.Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        foreach (var h in _handles) DestroyIcon(h);
    }
}
