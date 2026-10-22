using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace rNotice.Agent;

internal static class AgentInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];
}

/// <summary>Per-user state lives here. The program files are shared; nothing below is.</summary>
internal static class Paths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rNotice");

    public static string Logs => Path.Combine(Root, "logs");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Database => Path.Combine(Root, "rnotice.db");
}

/// <summary>
/// Plain daily log files. Titles and senders may be logged; snippets and bodies never are.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                var now = DateTimeOffset.UtcNow;
                File.AppendAllText(Path.Combine(Paths.Logs, $"rnotice-{now:yyyyMMdd}.log"),
                    $"{now:yyyy-MM-ddTHH:mm:ssZ} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the agent down.
        }
    }
}

/// <summary>
/// Per-user Run entry, the same approach as rMic's tray. The binaries are machine-wide but
/// autostart is not: an HKLM key would start rNotice for every user, and the app registration
/// only admits one.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "rNotice";

    private static string ExecutablePath => Environment.ProcessPath ?? "rNotice.exe";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains("rNotice.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns false if the registry could not be written.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Whether rNotice's windows keep out of screenshots and screen shares. On by default; the tray
/// switches it off when you need to show someone what rNotice looks like.
/// </summary>
internal static class CapturePolicy
{
    public static bool Hide { get; set; } = true;
}

internal static class NativeMethods
{
    private const uint WDA_MONITOR = 0x01;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>
    /// Keeps a window out of screen captures and shares. Windows 10 2004+ omits it entirely;
    /// older builds fall back to a black rectangle. Not a security measure, and whether Teams
    /// honours it is still to be checked (SPEC.md, <i>While you're screen sharing</i>).
    /// </summary>
    public static void ExcludeFromCapture(IntPtr hwnd) => SetCaptureExclusion(hwnd, CapturePolicy.Hide);

    /// <summary>Applies the setting to one window. Off clears the affinity, so screenshots work.</summary>
    public static void SetCaptureExclusion(IntPtr hwnd, bool exclude)
    {
        const uint WDA_NONE = 0x00;
        if (!exclude)
        {
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
            return;
        }
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    /// <summary>Flashes the window until it is focused. The substitute for stealing focus.</summary>
    public static void Flash(IntPtr hwnd)
    {
        const uint FLASHW_ALL = 0x03, FLASHW_TIMERNOFG = 0x0C;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref info);
    }
}
