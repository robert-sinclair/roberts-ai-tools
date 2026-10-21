using System.Windows;
using System.Windows.Threading;

namespace rNotice.Agent;

internal static class Program
{
    // Another launch (Start Menu, a second autostart) signals this instead of starting twice.
    private const string ShowQueueEvent = @"Local\rNotice.Agent.ShowQueue";

    [STAThread]
    private static int Main()
    {
        // One agent per logon session: one token holder, one store writer.
        using var single = new Mutex(initiallyOwned: true, @"Local\rNotice.Agent.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            if (EventWaitHandle.TryOpenExisting(ShowQueueEvent, out var show)) show.Set();
            return 0;
        }

        Log.Info($"rNotice {AgentInfo.Version} starting as {Environment.UserName}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Log and keep running. An assistant that dies silently is worse than one that
            // carries on after a bad poll.
            Log.Error("Unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
        };

        using var host = new AgentHost(app);
        using var showQueue = new EventWaitHandle(false, EventResetMode.AutoReset, ShowQueueEvent);
        ThreadPool.RegisterWaitForSingleObject(showQueue,
            (_, _) => app.Dispatcher.BeginInvoke(host.OpenQueue), null, Timeout.Infinite, executeOnlyOnce: false);

        app.Startup += (_, _) => host.Start();
        app.Run();

        Log.Info("rNotice exiting");
        return 0;
    }
}
