namespace rMic.Core;

/// <summary>Well-known locations. Machine state lives in ProgramData; tray preferences are per-user.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rMic");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public static string StateFile => Path.Combine(DataDirectory, "state.json");

    public static string LogFile => Path.Combine(DataDirectory, "service.log");

    public static string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rMic");

    public static string TraySettingsFile => Path.Combine(UserDataDirectory, "tray.json");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    public static void EnsureUserDataDirectory() => Directory.CreateDirectory(UserDataDirectory);
}
