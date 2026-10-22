using System.Windows;
using System.Windows.Threading;

namespace rNotice.Agent;

internal static class Program
{
    // Another launch (Start Menu, a second autostart) signals this instead of starting twice.
    private const string ShowQueueEvent = @"Local\rNotice.Agent.ShowQueue";

    // `rNotice.exe --preview-alert` asks the running copy to preview the next meeting's alert.
    private const string PreviewAlertEvent = @"Local\rNotice.Agent.PreviewAlert";

    [STAThread]
    private static int Main(string[] args)
    {
        // One agent per logon session: one token holder, one store writer.
        using var single = new Mutex(initiallyOwned: true, @"Local\rNotice.Agent.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            var signal = args.Contains("--preview-alert") ? PreviewAlertEvent : ShowQueueEvent;
            if (EventWaitHandle.TryOpenExisting(signal, out var handle)) handle.Set();
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
        using var previewAlert = new EventWaitHandle(false, EventResetMode.AutoReset, PreviewAlertEvent);
        ThreadPool.RegisterWaitForSingleObject(previewAlert,
            (_, _) => app.Dispatcher.BeginInvoke(host.PreviewMeetingAlert), null, Timeout.Infinite, executeOnlyOnce: false);

        app.Startup += (_, _) => host.Start();
        app.Run();

        Log.Info("rNotice exiting");
        return 0;
    }
}
