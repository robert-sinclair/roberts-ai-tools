namespace rMic.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One tray icon per logon session.
        using var single = new Mutex(initiallyOwned: true, @"Local\rMic.Tray.SingleInstance", out var isFirst);
        if (!isFirst)
            return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayContext();
        Application.Run(context);
    }
}
