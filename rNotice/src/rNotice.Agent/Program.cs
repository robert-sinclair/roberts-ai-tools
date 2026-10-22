using System.Windows;
using System.Windows.Threading;

namespace rNotice.Agent;

internal static class Program
{
    // A second launch signals the running copy instead of starting twice. With no argument it
    // opens the queue (Start Menu, a second autostart); the arguments ask for something else.
    private const string ShowQueueEvent = @"Local\rNotice.Agent.ShowQueue";
    private const string PreviewAlertEvent = @"Local\rNotice.Agent.PreviewAlert";
    private const string IgnoredSendersEvent = @"Local\rNotice.Agent.IgnoredSenders";

    [STAThread]
    private static int Main(string[] args)
    {
        // One agent per logon session: one token holder, one store writer.
        using var single = new Mutex(initiallyOwned: true, @"Local\rNotice.Agent.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            var signal = args.Contains("--preview-alert") ? PreviewAlertEvent
                : args.Contains("--ignored-senders") ? IgnoredSendersEvent
                : ShowQueueEvent;
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
        using var showQueue = Listen(app, ShowQueueEvent, host.OpenQueue);
        using var previewAlert = Listen(app, PreviewAlertEvent, host.PreviewMeetingAlert);
        using var ignoredSenders = Listen(app, IgnoredSendersEvent, host.OpenIgnoredSenders);

        app.Startup += (_, _) => host.Start();
        app.Run();

        Log.Info("rNotice exiting");
        return 0;
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread each time a second launch signals.</summary>
    private static EventWaitHandle Listen(Application app, string name, Action action)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        ThreadPool.RegisterWaitForSingleObject(handle,
            (_, _) => app.Dispatcher.BeginInvoke(action), null, Timeout.Infinite, executeOnlyOnce: false);
        return handle;
    }
}
