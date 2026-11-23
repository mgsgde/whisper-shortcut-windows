using System.Text;

namespace WhisperShortcut.Core;

/// <summary>Helpers of "Problem melden": secret redaction and the log tail.</summary>
public static class ProblemReport
{
    /// <summary>Masks Gemini and OpenAI keys and bearer tokens.</summary>
    public static string Redact(string line) => SecretRedactor.Redact(line);

    /// <summary>The log tail as a report section; "" when there are no lines. Lines are redacted.</summary>
    public static string LogSection(IEnumerable<string> lines)
    {
        var list = lines.Select(Redact).ToList();
        return list.Count == 0 ? "" : "## Log (last lines)\n```\n" + string.Join("\n", list) + "\n```";
    }

    /// <summary>
    /// The last <paramref name="lines"/> lines of the log, redacted. The file is being appended to by the app,
    /// so it is opened shared; a missing or unreadable file gives an empty list.
    /// </summary>
    public static IReadOnlyList<string> TailLog(string path, int lines = 50)
    {
        try
        {
            if (!File.Exists(path) || lines <= 0) return Array.Empty<string>();
            var queue = new Queue<string>(lines + 1);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                queue.Enqueue(Redact(line));
                if (queue.Count > lines) queue.Dequeue();
            }
            return queue.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
