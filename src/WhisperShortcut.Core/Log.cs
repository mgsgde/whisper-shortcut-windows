using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// Append-only text log, one line per event with timestamp. Rotates to <c>wsw.log.1</c> at
/// <see cref="RotateAtBytes"/> so a hung download cannot grow the file without bound. Never pass
/// the API key or full transcripts to it: log lengths only. Before <see cref="Init"/> all calls are no-ops.
/// </summary>
public static class Log
{
    /// <summary>Size at which the current file is renamed to <c>.1</c> (the previous <c>.1</c> is replaced).</summary>
    public const long RotateAtBytes = 2L * 1024 * 1024;

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

    /// <summary>Writes an ERROR line; for an exception its type, message and stack trace are written.</summary>
    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex == null ? message : $"{message} ({ex.GetType().Name}: {ex.Message})");
        if (ex?.StackTrace is { Length: > 0 } stack)
            Write("ERROR", stack);
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path == null || AppData.ResetDone) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateIfNeeded(_path);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message.Replace('\n', ' ').Replace('\r', ' ')}{Environment.NewLine}";
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    /// <summary>Renames a large log to <c>.1</c> so the next write starts a fresh file.</summary>
    internal static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < RotateAtBytes) return;
            var bak = path + ".1";
            File.Delete(bak);
            File.Move(path, bak);
        }
        catch
        {
            // A failed rotate must not block the write that follows.
        }
    }
}
