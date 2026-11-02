using System.Runtime.InteropServices;
using System.Text;

namespace WhisperShortcut.Core.Mcp;

/// <summary>The problem report file an AI app can save with report_problem: Markdown the user reviews before reporting anything.</summary>
public static class McpProblemReport
{
    private const string Footer = "Von einer KI-App über WhisperShortcut erstellt. Es wurde nichts gesendet. Wer das melden möchte: Text prüfen, Privates entfernen und unter https://github.com/mgsgde/whisper-shortcut-windows/issues/new einfügen.";

    private static string OneLine(string s) => ProblemReport.Redact(s).ReplaceLineEndings(" ").Trim();

    private static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in title)
            if (!invalid.Contains(c) && !char.IsControl(c)) sb.Append(c);
        var name = sb.ToString().Trim();
        if (name.Length > 60) name = name[..60];
        name = name.TrimEnd(' ', '.');
        return name.Length == 0 ? "Problem" : name;
    }

    private static string EngineName(Engine e) => e switch { Engine.Local => "lokal", Engine.OpenAI => "OpenAI", _ => "Gemini" };

    /// <summary>
    /// Writes "&lt;yyyy-MM-dd_HH-mm-ss&gt; &lt;safe title&gt;.md" into <paramref name="directory"/> and returns its path. Every
    /// user-provided field goes through <see cref="ProblemReport.Redact"/>; the environment block holds no keys and no glossary.
    /// </summary>
    public static string Write(string directory, string title, string whatHappens, IReadOnlyList<string> examples, string? suspectedCause, string whyNotSettings, Settings settings, string appVersion, DateTime localNow)
    {
        var heading = OneLine(title);
        var sb = new StringBuilder();
        sb.Append("# ").Append(heading).Append("\n\n");
        sb.Append("## Was passiert\n\n").Append(ProblemReport.Redact(whatHappens).Trim()).Append("\n\n");
        if (examples.Count > 0)
        {
            sb.Append("## Beispiele\n\n");
            foreach (var e in examples) sb.Append("- ").Append(OneLine(e)).Append('\n');
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(suspectedCause))
            sb.Append("## Vermutete Ursache\n\n").Append(ProblemReport.Redact(suspectedCause).Trim()).Append("\n\n");
        sb.Append("## Warum keine Einstellung hilft\n\n").Append(ProblemReport.Redact(whyNotSettings).Trim()).Append("\n\n");
        sb.Append("---\n\n");
        sb.Append($"- Version: {appVersion}\n");
        sb.Append($"- Windows: {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})\n");
        sb.Append($"- Modus: {(settings.OfflineMode ? "offline" : "online")}\n");
        sb.Append($"- Spracherkennung: {EngineName(settings.SpeechEngine)}\n");
        sb.Append($"- Texte bearbeiten: {EngineName(settings.TextEngine)}\n");
        sb.Append($"- Diktat-Modell (Gemini): {settings.TranscriptionModel}\n");
        sb.Append($"- Diktat-Modell (OpenAI): {settings.OpenAiTranscriptionModel}\n");
        sb.Append($"- Prompt-Modell (Gemini): {settings.PromptModel}\n");
        sb.Append($"- Prompt-Modell (OpenAI): {settings.OpenAiPromptModel}\n");
        sb.Append($"- Offline-Sprachmodell: {(settings.OfflineLlmId.Length == 0 ? "-" : settings.OfflineLlmId)}\n\n");
        sb.Append(Footer).Append('\n');

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{localNow:yyyy-MM-dd_HH-mm-ss} {SafeName(heading)}.md");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }
}
