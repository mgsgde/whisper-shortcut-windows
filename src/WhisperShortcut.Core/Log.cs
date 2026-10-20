using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// Append-only text log, one line per event with timestamp. Never pass the API key or full
/// transcripts to it: log lengths only. Before <see cref="Init"/> all calls are no-ops.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>Default location: %LOCALAPPDATA%\WhisperShortcut\wsw.log.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperShortcut", "wsw.log");

    /// <summary>Sets the log file (created with its directory on first write). Null disables logging.</summary>
    public static void Init(string? path)
    {
        lock (Gate) _path = path;
    }

    /// <summary>Writes an INFO line.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Writes a WARN line.</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an ERROR line; for an exception only its type and message are written.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message} ({ex.GetType().Name}: {ex.Message})");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path == null) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message.Replace('\n', ' ').Replace('\r', ' ')}{Environment.NewLine}";
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
