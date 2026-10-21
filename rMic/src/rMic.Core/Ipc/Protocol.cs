using System.Text.Json;
using System.Text.Json.Serialization;

namespace rMic.Core.Ipc;

public enum ControlCommand
{
    Status,

    /// <summary>Enable every capture device rMic can see.</summary>
    EnableAll,

    /// <summary>Disable every capture device, including any that appear later.</summary>
    DisableAll,

    /// <summary>All off if anything is still on, otherwise all on.</summary>
    ToggleAll,

    EnableDevice,
    DisableDevice,
    ToggleDevice
}

public sealed class ControlRequest
{
    public string Command { get; set; } = nameof(ControlCommand.Status);

    /// <summary>Required by the per-device commands, ignored by the rest.</summary>
    public string? InstanceId { get; set; }

    public static ControlRequest For(ControlCommand command, string? instanceId = null) =>
        new() { Command = command.ToString(), InstanceId = instanceId };

    public bool TryParseCommand(out ControlCommand command)
        => Enum.TryParse(Command, ignoreCase: true, out command);
}

public sealed class DeviceInfo
{
    public string InstanceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The device's actual state right now.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether rMic intends to hold this device off.</summary>
    public bool DesiredDisabled { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;

    public string? Error { get; set; }

    /// <summary>True when "all off" is a standing instruction that also covers new arrivals.</summary>
    public bool AllOffMode { get; set; }

    /// <summary>True when the service re-asserts the off state on a timer.</summary>
    public bool Enforcing { get; set; }

    public List<DeviceInfo> Devices { get; set; } = new();

    public string ServiceVersion { get; set; } = string.Empty;

    [JsonIgnore]
    public int EnabledCount => Devices.Count(d => d.Enabled);

    [JsonIgnore]
    public int DisabledCount => Devices.Count(d => !d.Enabled);

    [JsonIgnore]
    public bool AnyEnabled => Devices.Any(d => d.Enabled);

    [JsonIgnore]
    public bool AnyDisabled => Devices.Any(d => !d.Enabled);

    [JsonIgnore]
    public bool AllEnabled => Devices.Count > 0 && !AnyDisabled;

    [JsonIgnore]
    public bool AllDisabled => Devices.Count > 0 && !AnyEnabled;

    public static ControlResponse Failure(string error) => new() { Ok = false, Error = error };
}

public static class Protocol
{
    /// <summary>Full path is \\.\pipe\rMic.Control.</summary>
    public const string PipeName = "rMic.Control";

    public const string ServiceName = "rMic";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
