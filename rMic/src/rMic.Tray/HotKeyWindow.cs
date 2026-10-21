using System.Runtime.InteropServices;

namespace rMic.Tray;

/// <summary>
/// Message-only window that owns the global hotkey (Ctrl+Alt+Shift+M).
/// </summary>
public sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0xB01D;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint VK_M = 0x4D;

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
        CreateHandle(new CreateParams { Parent = HwndMessage });
    }

    public static string Description => "Ctrl+Alt+Shift+M";

    /// <summary>Returns false when another application already owns the combination.</summary>
    public bool Register()
    {
        if (_registered)
            return true;

        _registered = RegisterHotKey(Handle, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_M);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        UnregisterHotKey(Handle, HotKeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
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
        Unregister();
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
