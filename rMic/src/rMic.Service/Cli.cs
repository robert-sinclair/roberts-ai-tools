using rMic.Core;
using rMic.Core.Devices;
using rMic.Core.Ipc;

namespace rMic.Service;

/// <summary>
/// Maintenance and diagnostic commands, used by the install scripts and for troubleshooting.
/// State-changing verbs prefer the running service; they only fall back to touching devices
/// directly (which requires elevation) when the service is not reachable.
/// </summary>
public static class Cli
{
    public static readonly string[] Verbs =
    {
        "--help", "-h", "/?", "--version",
        "--list", "--list-all", "--status",
        "--enable", "--enable-all", "--disable", "--disable-all",
        "--enable-device", "--disable-device", "--toggle-device"
    };

    public static bool IsVerb(string arg) => Verbs.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static int Run(string[] args, Logger log, ServiceConfig config)
    {
        log.EchoToConsole = true;
        var verb = args[0].ToLowerInvariant();
        var argument = args.Length > 1 ? args[1] : null;

        switch (verb)
        {
            case "--help":
            case "-h":
            case "/?":
                PrintHelp();
                return 0;

            case "--version":
                Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                return 0;

            case "--list":
                return List(config, log, matchingOnly: true);

            case "--list-all":
                return List(config, log, matchingOnly: false);

            case "--status":
                return Act(ControlCommand.Status, null, log, config, requiresElevation: false);

            case "--enable":
            case "--enable-all":
                return Act(ControlCommand.EnableAll, null, log, config, requiresElevation: true);

            case "--disable":
            case "--disable-all":
                return Act(ControlCommand.DisableAll, null, log, config, requiresElevation: true);

            case "--enable-device":
            case "--disable-device":
            case "--toggle-device":
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    Console.Error.WriteLine(
                        $"{verb} needs a device instance ID. Run --list to see them, and quote the ID.");
                    return 2;
                }

                var command = verb switch
                {
                    "--enable-device" => ControlCommand.EnableDevice,
                    "--disable-device" => ControlCommand.DisableDevice,
                    _ => ControlCommand.ToggleDevice
                };

                return Act(command, argument, log, config, requiresElevation: true);
            }

            default:
                Console.Error.WriteLine($"Unknown option '{args[0]}'.");
                PrintHelp();
                return 2;
        }
    }

    /// <summary>
    /// Routes through the running service when there is one, so the service's in-memory state
    /// never drifts from what is on disk, and only touches devices directly when it is stopped.
    /// </summary>
    private static int Act(
        ControlCommand command, string? instanceId, Logger log, ServiceConfig config, bool requiresElevation)
    {
        ControlResponse result;

        try
        {
            result = ControlClient.SendAsync(command, instanceId, timeoutMs: 30000).GetAwaiter().GetResult();
            Console.WriteLine("(via the running rMic service)");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            if (requiresElevation && !IsElevated())
            {
                Console.Error.WriteLine(
                    "The rMic service is not reachable and this command changes device state directly, " +
                    "so it must be run as Administrator.");
                return 5;
            }

            result = new MicSwitch(log, config).ExecuteFromCli(command, instanceId);
        }

        PrintSummary(result);

        if (!result.Ok)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }
        return 0;
    }

    private static void PrintSummary(ControlResponse result)
    {
        var state = result.Devices.Count == 0
            ? "no capture devices detected"
            : result.AllEnabled
                ? "all enabled"
                : result.AllDisabled
                    ? "all disabled"
                    : $"{result.DisabledCount} of {result.Devices.Count} disabled";

        Console.WriteLine($"Microphones: {state}{(result.AllOffMode ? "  [all-off mode]" : string.Empty)}");
        PrintDevices(result.Devices.Select(d =>
            (Name: d.Name, InstanceId: d.InstanceId, Enabled: d.Enabled, Held: d.DesiredDisabled)));
    }

    private static int List(ServiceConfig config, Logger log, bool matchingOnly)
    {
        var effective = matchingOnly
            ? config
            : new ServiceConfig
            {
                CaptureInstanceIdPatterns = new List<string> { ".*" },
                ExtraDeviceClassGuids = config.ExtraDeviceClassGuids
            };

        var devices = new CaptureDeviceManager(effective, log.Warn).Enumerate();

        Console.WriteLine(matchingOnly
            ? "Capture devices rMic will switch:"
            : "All devices in the scanned classes (use this to build config patterns):");

        if (devices.Count == 0)
        {
            Console.WriteLine("  (none)");
            return matchingOnly ? 1 : 0;
        }

        PrintDevices(devices.Select(d =>
            (Name: d.Name, InstanceId: d.InstanceId, Enabled: d.IsEnabled, Held: false)));
        return 0;
    }

    private static void PrintDevices(
        IEnumerable<(string Name, string InstanceId, bool Enabled, bool Held)> devices)
    {
        foreach (var device in devices)
        {
            var held = device.Held && device.Enabled ? "  (held off, not yet applied)" : string.Empty;
            Console.WriteLine($"  [{(device.Enabled ? "on " : "OFF")}] {device.Name}{held}");
            Console.WriteLine($"         {device.InstanceId}");
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            rMic service — driver-level microphone kill switch.

            Usage:
              rMic.Service.exe                        Run as a Windows service (started by the SCM).
              rMic.Service.exe --console              Run in the foreground for debugging.
              rMic.Service.exe --list                 List the capture devices rMic will switch.
              rMic.Service.exe --list-all             List every device in the scanned classes.
              rMic.Service.exe --status               Show the state of every capture device.
              rMic.Service.exe --disable-all          Disable every capture device (Administrator).
              rMic.Service.exe --enable-all           Enable every capture device (Administrator).
              rMic.Service.exe --disable-device <id>  Disable one device by instance ID.
              rMic.Service.exe --enable-device <id>   Enable one device by instance ID.
              rMic.Service.exe --toggle-device <id>   Flip one device by instance ID.
              rMic.Service.exe --version              Print the version.

            --disable and --enable are accepted as aliases for the -all forms.
            Instance IDs come from --list and contain backslashes, so quote them.

            Config:  %ProgramData%\rMic\config.json
            State:   %ProgramData%\rMic\state.json
            Log:     %ProgramData%\rMic\service.log
            """);
    }
}
