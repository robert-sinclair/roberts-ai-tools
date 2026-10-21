using System.Runtime.InteropServices;

namespace rNotice.Probe;

internal static class NativeMethods
{
    // DllImport rather than LibraryImport: the source-generated form requires AllowUnsafeBlocks
    // (SYSLIB1062), and a parameterless call returning a handle gains nothing from it.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// The window the broker parents its sign-in dialog to. Without a parent the dialog can
    /// open behind the console, which looks like a hang.
    /// </summary>
    public static IntPtr GetConsoleWindowHandle() => GetConsoleWindow();
}
