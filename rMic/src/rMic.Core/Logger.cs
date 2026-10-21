namespace rMic.Core;

/// <summary>
/// Minimal rolling file log. A service that cannot log is still a service, so every failure
/// here is swallowed deliberately.
/// </summary>
public sealed class Logger
{
    private const long MaxBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;

    public Logger(string path) => _path = path;

    /// <summary>Also echo to stdout, which is useful when running the service with --console.</summary>
    public bool EchoToConsole { get; set; }

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";

        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                Roll();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Never let logging take the service down.
            }
        }

        if (EchoToConsole)
            Console.WriteLine(line);
    }

    private void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        var previous = _path + ".1";
        if (File.Exists(previous))
            File.Delete(previous);
        File.Move(_path, previous);
    }
}
