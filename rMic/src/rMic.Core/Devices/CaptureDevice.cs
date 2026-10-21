namespace rMic.Core.Devices;

/// <summary>A microphone/capture endpoint that rMic is willing to switch on or off.</summary>
/// <param name="InstanceId">Device instance ID, e.g. <c>SWD\MMDEVAPI\{0.0.1.00000000}.{guid}</c>.</param>
/// <param name="Name">Friendly name as shown in Device Manager.</param>
/// <param name="IsEnabled">False when the devnode carries the "disabled" problem code.</param>
/// <param name="StatusKnown">
/// False when the devnode status could not be read. Callers should not skip work based on
/// <paramref name="IsEnabled"/> when this is false.
/// </param>
public sealed record CaptureDevice(string InstanceId, string Name, bool IsEnabled, bool StatusKnown);

/// <summary>A device rMic tried to switch but could not.</summary>
public sealed record DeviceFailure(CaptureDevice Device, string Error);

/// <summary>Outcome of a bulk enable/disable pass.</summary>
public sealed class ApplyResult
{
    /// <summary>Devices whose state this pass actually changed.</summary>
    public List<CaptureDevice> Changed { get; } = new();

    /// <summary>Devices that were already in the requested state and were left alone.</summary>
    public List<CaptureDevice> AlreadyInState { get; } = new();

    /// <summary>Devices that matched but could not be switched.</summary>
    public List<DeviceFailure> Failed { get; } = new();

    public bool AnyFailures => Failed.Count > 0;

    public int Matched => Changed.Count + AlreadyInState.Count + Failed.Count;

    /// <summary>
    /// Every device this pass was aimed at, whatever the outcome. Failures are included on
    /// purpose: rMic still intends to hold them off, so enforcement should keep trying.
    /// </summary>
    public IEnumerable<CaptureDevice> AllMatched =>
        Changed.Concat(AlreadyInState).Concat(Failed.Select(f => f.Device));

    public string Describe()
    {
        var parts = new List<string>
        {
            $"changed={Changed.Count}",
            $"unchanged={AlreadyInState.Count}",
            $"failed={Failed.Count}"
        };
        if (Failed.Count > 0)
            parts.Add("errors: " + string.Join(" | ", Failed.Select(f => $"{f.Device.Name}: {f.Error}")));
        return string.Join(", ", parts);
    }
}
