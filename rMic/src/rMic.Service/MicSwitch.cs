using System.Reflection;
using rMic.Core;
using rMic.Core.Devices;
using rMic.Core.Ipc;

namespace rMic.Service;

/// <summary>
/// The switch itself: owns the desired state, the device manager and the lock that keeps
/// pipe requests from racing the enforcement timer.
/// </summary>
/// <remarks>
/// State is per device. Turning everything off also sets a standing "all off" instruction so
/// microphones plugged in later are caught too; turning any single device back on clears that
/// instruction, because from then on the user has expressed a more specific intent than
/// "nothing may listen".
/// </remarks>
public sealed class MicSwitch
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Logger _log;
    private readonly ServiceConfig _config;
    private readonly StateStore _store;
    private readonly CaptureDeviceManager _devices;
    private readonly MicState _state;

    public MicSwitch(Logger log, ServiceConfig config)
    {
        _log = log;
        _config = config;
        _store = new StateStore(AppPaths.StateFile, log);
        _devices = new CaptureDeviceManager(config, log.Warn);
        _state = _store.Load();
    }

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        if (!request.TryParseCommand(out var command))
            return ControlResponse.Failure($"Unknown command '{request.Command}'.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Execute(command, request.InstanceId, "request");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Direct entry point for the command line when no service is running.</summary>
    public ControlResponse ExecuteFromCli(ControlCommand command, string? instanceId = null)
        => Execute(command, instanceId, "command line");

    /// <summary>Re-applies the persisted state, e.g. at service start after a reboot.</summary>
    public async Task ReassertAsync(string reason, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.AllOffMode && _state.DisabledDevices.Count == 0)
            {
                _log.Info($"Nothing held off on {reason}; leaving all capture devices as they are.");
                return;
            }

            var result = EnforceCore();
            _log.Info($"Re-asserted on {reason} " +
                      $"(all-off mode: {_state.AllOffMode}, held devices: {_state.DisabledDevices.Count}): " +
                      (result?.Describe() ?? "nothing to do"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Switches off anything that has come back while it is meant to be off — a newly plugged-in
    /// USB microphone under all-off mode, or a held device re-enabled behind our back.
    /// </summary>
    public async Task EnforceAsync(CancellationToken ct)
    {
        if (!_config.EnforceWhenDisabled)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = EnforceCore();
            if (result is { Changed.Count: > 0 })
            {
                _log.Info($"Enforcement switched off {result.Changed.Count} capture device(s): " +
                          string.Join(", ", result.Changed.Select(d => d.Name)));
            }

            foreach (var failure in result?.Failed ?? new List<DeviceFailure>())
                _log.Warn($"Enforcement could not disable '{failure.Device.Name}': {failure.Error}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private ControlResponse Execute(ControlCommand command, string? instanceId, string reason)
    {
        switch (command)
        {
            case ControlCommand.Status:
                return BuildResponse();

            case ControlCommand.EnableAll:
                return SetAll(enable: true, reason);

            case ControlCommand.DisableAll:
                return SetAll(enable: false, reason);

            case ControlCommand.ToggleAll:
            {
                var present = _devices.Enumerate();
                if (present.Count == 0)
                {
                    _log.Warn("Toggle-all requested but no capture devices matched the configured patterns.");
                    return BuildResponse();
                }
                // Anything still listening means the useful action is to silence everything.
                return SetAll(enable: !present.Any(d => d.IsEnabled), reason);
            }

            case ControlCommand.EnableDevice:
            case ControlCommand.DisableDevice:
            case ControlCommand.ToggleDevice:
            {
                if (string.IsNullOrWhiteSpace(instanceId))
                    return ControlResponse.Failure($"{command} requires a device instance ID.");

                var device = _devices.Enumerate()
                    .FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

                if (device is null)
                    return ControlResponse.Failure($"No capture device with instance ID '{instanceId}' is present.");

                var enable = command switch
                {
                    ControlCommand.EnableDevice => true,
                    ControlCommand.DisableDevice => false,
                    _ => !device.IsEnabled
                };

                return SetOne(device, enable, reason);
            }

            default:
                return ControlResponse.Failure($"Command '{command}' is not implemented.");
        }
    }

    private ControlResponse SetAll(bool enable, string reason)
    {
        ApplyResult result;

        if (enable)
        {
            _state.AllOffMode = false;
            _state.DisabledDevices.Clear();
            result = _devices.Apply(enable: true);
        }
        else
        {
            _state.AllOffMode = true;
            result = _devices.Apply(enable: false);
            Remember(result.AllMatched);
        }

        _store.Save(_state);

        _log.Info($"All microphones {(enable ? "ENABLED" : "DISABLED")} by {reason}: {result.Describe()}");

        if (result.Matched == 0)
            _log.Warn("No capture devices matched the configured patterns. " +
                      "Run 'rMic.Service.exe --list' to see what is present.");

        return Finish(result);
    }

    private ControlResponse SetOne(CaptureDevice device, bool enable, string reason)
    {
        if (enable)
        {
            // A specific device being turned back on is a more specific intent than "all off",
            // so stop enforcing the blanket rule that would immediately undo it.
            if (_state.AllOffMode)
            {
                _log.Info($"Leaving all-off mode because '{device.Name}' was turned back on. " +
                          "Microphones plugged in from now on will not be disabled automatically.");
                _state.AllOffMode = false;
            }
            _state.DisabledDevices.RemoveAll(id =>
                string.Equals(id, device.InstanceId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Remember(new[] { device });
        }

        var result = _devices.Apply(enable, new[] { device.InstanceId });
        _store.Save(_state);

        _log.Info($"'{device.Name}' {(enable ? "ENABLED" : "DISABLED")} by {reason}: {result.Describe()}");

        return Finish(result);
    }

    /// <summary>Re-applies the held-off set. Returns null when nothing is being held.</summary>
    private ApplyResult? EnforceCore()
    {
        if (!_state.AllOffMode && _state.DisabledDevices.Count == 0)
            return null;

        var result = _state.AllOffMode
            ? _devices.Apply(enable: false)
            : _devices.Apply(enable: false, _state.DisabledDevices.ToList());

        var added = _state.AllOffMode ? Remember(result.AllMatched) : 0;

        if (result.Changed.Count > 0 || added > 0)
            _store.Save(_state);

        return result;
    }

    /// <summary>Adds devices to the held-off set. Returns how many were new.</summary>
    private int Remember(IEnumerable<CaptureDevice> devices)
    {
        var added = 0;
        foreach (var device in devices)
        {
            if (!IsHeld(device.InstanceId))
            {
                _state.DisabledDevices.Add(device.InstanceId);
                added++;
            }
        }
        return added;
    }

    private bool IsHeld(string instanceId) =>
        _state.DisabledDevices.Contains(instanceId, StringComparer.OrdinalIgnoreCase);

    private ControlResponse Finish(ApplyResult result)
    {
        var response = BuildResponse();

        if (result.AnyFailures)
        {
            response.Ok = false;
            response.Error = string.Join("; ", result.Failed.Select(f => $"{f.Device.Name}: {f.Error}"));
        }

        return response;
    }

    private ControlResponse BuildResponse() => new()
    {
        Ok = true,
        AllOffMode = _state.AllOffMode,
        Enforcing = _config.EnforceWhenDisabled,
        ServiceVersion = Version,
        Devices = _devices.Enumerate()
            .Select(d => new DeviceInfo
            {
                InstanceId = d.InstanceId,
                Name = d.Name,
                Enabled = d.IsEnabled,
                DesiredDisabled = _state.AllOffMode || IsHeld(d.InstanceId)
            })
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList()
    };
}
