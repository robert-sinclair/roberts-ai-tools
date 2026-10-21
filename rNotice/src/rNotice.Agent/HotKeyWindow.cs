using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace rNotice.Agent;

/// <summary>
/// Message-only window that owns the global hotkey (Ctrl+Alt+Shift+N) for the queue window.
/// Same shape as rMic's.
/// </summary>
internal sealed class HotKeyWindow : WinForms.NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0xB02E;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint VK_N = 0x4E;

    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Action _onPressed;
    private bool _registered;

    public HotKeyWindow(Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new WinForms.CreateParams { Parent = HwndMessage });
    }

    public static string Description => "Ctrl+Alt+Shift+N";

    /// <summary>Returns false when another application already owns the combination.</summary>
    public bool Register()
    {
        if (_registered)
            return true;

        _registered = RegisterHotKey(Handle, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_N);
        return _registered;
    }

    protected override void WndProc(ref WinForms.Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
        {
            _onPressed();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(Handle, HotKeyId);
        _registered = false;
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
