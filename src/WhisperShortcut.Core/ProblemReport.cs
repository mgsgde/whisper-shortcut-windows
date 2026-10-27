using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>What the user's problem report says about the installation (no content, no secrets).</summary>
public sealed record ProblemReportInfo(
    string AppVersion, string WindowsVersion, string Architecture, bool Offline,
    string TranscriptionModel, string PromptModel, string OfflineLlm, string ProfileName);

/// <summary>Builds the prefilled GitHub issue behind "Problem melden".</summary>
public static partial class ProblemReport
{
    /// <summary>Longest issue URL we open; browsers and GitHub choke far above this.</summary>
    public const int MaxUrlLength = 7000;

    /// <summary>Body used instead of the report when it does not fit into the URL (the UI copies the report to the clipboard).</summary>
    public const string ClipboardHint = "Bitte den Bericht aus der Zwischenablage hier einfügen (Strg+V).";

    private const string IssuesNew = "https://github.com/mgsgde/whisper-shortcut-windows/issues/new";

    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{20,}")]
    private static partial Regex GeminiKey();

    /// <summary>Masks anything that looks like a Gemini API key.</summary>
    public static string Redact(string line) => GeminiKey().Replace(line, "AIza…");

    /// <summary>The Markdown body: an empty "what happened" section to type into, the environment and the log tail.</summary>
    public static string Body(ProblemReportInfo info, IEnumerable<string> logLines)
    {
        var sb = new StringBuilder();
        sb.Append("**Was ist passiert?**\n\n\n\n");
        sb.Append("**Umgebung**\n\n");
        sb.Append($"- Version: {info.AppVersion}\n");
        sb.Append($"- Windows: {info.WindowsVersion} ({info.Architecture})\n");
        sb.Append($"- Modus: {(info.Offline ? "offline" : "online")}\n");
        sb.Append($"- Diktat-Modell: {info.TranscriptionModel}\n");
        sb.Append($"- Prompt-Modell: {info.PromptModel}\n");
        sb.Append($"- Offline-Sprachmodell: {(info.OfflineLlm.Length == 0 ? "-" : info.OfflineLlm)}\n");
        sb.Append($"- Profil: {info.ProfileName}\n\n");
        sb.Append("**Protokoll (letzte Zeilen)**\n\n```\n");
        foreach (var line in logLines) sb.Append(Redact(line)).Append('\n');
        sb.Append("```\n");
        return sb.ToString();
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

    /// <summary>
    /// The prefilled new-issue URL. When the full body would make the URL longer than <see cref="MaxUrlLength"/>
    /// the URL carries only <see cref="ClipboardHint"/> and BodyInUrl is false: the caller puts the body on the clipboard.
    /// </summary>
    public static (string Url, bool BodyInUrl) IssueUrl(string title, string body)
    {
        string Make(string b) => $"{IssuesNew}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(b)}";
        var full = Make(body);
        return full.Length <= MaxUrlLength ? (full, true) : (Make(ClipboardHint), false);
    }
}
