using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// Append-only text log, one line per event with timestamp. Never pass the API key or full
/// transcripts to it: log lengths only. Before <see cref="Init"/> all calls are no-ops.
/// Rotates to <c>*.1</c> when the file exceeds <see cref="MaxFileBytes"/> so a long-running
/// install cannot grow without bound.
/// </summary>
public static class Log
{
    /// <summary>Size at which the current file is renamed to <c>*.1</c> (one previous file is kept).</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

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

    /// <summary>Writes an ERROR line; for an exception its type, message and flattened stack are written.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message} ({ex.GetType().Name}: {ex.Message}){FlattenStack(ex)}");

    private static string FlattenStack(Exception ex)
    {
        var stack = ex.StackTrace;
        return string.IsNullOrEmpty(stack) ? "" : " " + OneLine(stack);
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ');

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
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {OneLine(SecretRedactor.Redact(message))}{Environment.NewLine}";
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes) return;
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
