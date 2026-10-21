using System.Runtime.InteropServices;

namespace rNotice.Probe;

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    /// <summary>
    /// The window the broker parents its sign-in dialog to. Without a parent the dialog can
    /// open behind the console, which looks like a hang.
    /// </summary>
    public static IntPtr GetConsoleWindowHandle() => GetConsoleWindow();
}
