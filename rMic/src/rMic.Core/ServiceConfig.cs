using System.Text.Json;

namespace rMic.Core;

/// <summary>
/// Machine-wide settings, written to %ProgramData%\rMic\config.json on first run so the
/// defaults are discoverable and editable without a rebuild.
/// </summary>
public sealed class ServiceConfig
{
    /// <summary>
    /// Device instance IDs to treat as microphones. The default matches Windows audio capture
    /// endpoints: MMDEVAPI endpoint IDs encode data flow in the second field, where
    /// <c>{0.0.0.</c> is render (speakers) and <c>{0.0.1.</c> is capture (microphones).
    /// </summary>
    public List<string> CaptureInstanceIdPatterns { get; set; } = new()
    {
        @"^SWD\\MMDEVAPI\\\{0\.0\.1\."
    };

    /// <summary>Instance IDs to leave alone even when they match an include pattern.</summary>
    public List<string> ExcludeInstanceIdPatterns { get; set; } = new();

    /// <summary>
    /// Extra device setup class GUIDs to scan alongside AudioEndpoint. Add
    /// <c>{4d36e96c-e325-11ce-bfc1-08002be10318}</c> (MEDIA) if you also want to target a
    /// USB mic's parent device node; pair it with a matching include pattern.
    /// </summary>
    public List<string> ExtraDeviceClassGuids { get; set; } = new();

    /// <summary>
    /// How often, while the mic is off, to re-scan and switch off any capture device that has
    /// appeared or come back. This is what makes the off state stick when hardware is hot-plugged.
    /// </summary>
    public int EnforceIntervalSeconds { get; set; } = 5;

    /// <summary>Set false to make "off" a one-shot action rather than a maintained state.</summary>
    public bool EnforceWhenDisabled { get; set; } = true;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static ServiceConfig LoadOrCreate(string path, Logger log)
    {
        try
        {
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path), SerializerOptions);
                if (config is not null)
                {
                    config.Normalize();
                    return config;
                }
                log.Warn($"Config at {path} was empty; falling back to defaults.");
            }
        }
        catch (Exception ex)
        {
            log.Error($"Could not read config at {path}; falling back to defaults", ex);
            return new ServiceConfig();
        }

        var defaults = new ServiceConfig();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, SerializerOptions));
            log.Info($"Wrote default config to {path}.");
        }
        catch (Exception ex)
        {
            log.Error($"Could not write default config to {path}", ex);
        }
        return defaults;
    }

    private void Normalize()
    {
        if (CaptureInstanceIdPatterns.Count == 0)
            CaptureInstanceIdPatterns = new ServiceConfig().CaptureInstanceIdPatterns;

        EnforceIntervalSeconds = Math.Clamp(EnforceIntervalSeconds, 1, 3600);
    }
}
