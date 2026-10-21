using System.Text.Json;

namespace rMic.Core;

/// <summary>Persisted desired state. Survives reboots, so "off" stays off until you say otherwise.</summary>
public sealed class MicState
{
    /// <summary>
    /// A standing instruction that every capture device stays off, including ones plugged in
    /// later. Set by "turn all off"; cleared as soon as any single device is turned back on,
    /// so enforcement never fights a deliberate choice.
    /// </summary>
    public bool AllOffMode { get; set; }

    /// <summary>Instance IDs rMic holds disabled. Devices not listed here are left alone.</summary>
    public List<string> DisabledDevices { get; set; } = new();

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Logger _log;

    public StateStore(string path, Logger log)
    {
        _path = path;
        _log = log;
    }

    public MicState Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var state = JsonSerializer.Deserialize<MicState>(File.ReadAllText(_path), SerializerOptions);
                    if (state is not null)
                        return state;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Could not read state at {_path}; assuming microphone enabled", ex);
            }
            return new MicState();
        }
    }

    public void Save(MicState state)
    {
        lock (_gate)
        {
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Write-then-replace so a crash mid-write cannot leave truncated state behind.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, SerializerOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error($"Could not write state to {_path}", ex);
            }
        }
    }
}
