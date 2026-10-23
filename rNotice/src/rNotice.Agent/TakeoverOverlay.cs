using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace rNotice.Agent;

/// <summary>
/// The takeover's dim: one translucent black window per monitor, under the meeting alert.
/// Double-click it to clear it; the alert stays.
///
/// It takes the mouse — clicks land on the dim, not on what's behind — but not the keyboard:
/// Windows won't let a background app take focus, so typing still goes where it was going.
/// It ignores clicks for the arming delay after it appears, so a click already on its way
/// can't clear a dim you never saw.
/// </summary>
internal sealed class TakeoverOverlay : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly System.Drawing.Rectangle _bounds;
    private bool _armed;

    public TakeoverOverlay(WinForms.Screen screen, double dim, bool showHint, TimeSpan arming, Action onDismiss)
    {
        _bounds = screen.Bounds;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "rNotice dim";
        Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(dim, 0.1, 0.9) * 255), 0, 0, 0));

        // Rough placement in device-independent units; SourceInitialized puts it exactly on the
        // monitor in physical pixels, which is what a mixed-DPI setup needs.
        Left = _bounds.Left;
        Top = _bounds.Top;
        Width = _bounds.Width;
        Height = _bounds.Height;

        if (showHint)
        {
            Content = new Grid
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Double-click the dark area to clear it. The meeting window stays.",
                        Foreground = Brushes.White, Opacity = 0.8, FontSize = 16, FontFamily = new FontFamily("Segoe UI"),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, 48)
                    }
                }
            };
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.ExcludeFromCapture(hwnd);
            Place(hwnd);
        };
        // Moving onto a monitor with a different scale makes WPF resize the window; put it back.
        DpiChanged += (_, _) => Place(new WindowInteropHelper(this).Handle);

        var timer = new DispatcherTimer { Interval = arming };
        timer.Tick += (_, _) => { _armed = true; timer.Stop(); };
        timer.Start();

        MouseDoubleClick += (_, e) =>
        {
            if (!_armed) return;
            e.Handled = true;
            onDismiss();
        };
    }

    private void Place(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
}
