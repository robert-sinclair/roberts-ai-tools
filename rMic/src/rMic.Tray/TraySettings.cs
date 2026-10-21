using System.Text.Json;
using rMic.Core;

namespace rMic.Tray;

/// <summary>Per-user tray preferences. Nothing here affects the switch itself.</summary>
public sealed class TraySettings
{
    public bool ShowNotifications { get; set; } = true;

    public bool HotKeyEnabled { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.TraySettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<TraySettings>(
                    File.ReadAllText(AppPaths.TraySettingsFile), Options);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable preferences are not worth interrupting the user over.
        }
        return new TraySettings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureUserDataDirectory();
            File.WriteAllText(AppPaths.TraySettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Preferences are a convenience; failing to persist them must not break the tray.
        }
    }
}
