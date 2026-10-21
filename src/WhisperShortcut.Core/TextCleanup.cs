using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Port of TextProcessingUtility.normalizeTranscriptionText / discardingImplausibleTranscript and
/// ClipboardManager.formatTranscription. Line breaks and indentation (bullet lists) survive.
/// </summary>
public static class TextCleanup
{
    private static readonly Regex ManyNewlines = new("\n{3,}", RegexOptions.Compiled);
    private static readonly Regex TrailingWhitespace = new(@"\s+$", RegexOptions.Compiled);
    private static readonly Regex SpacesAndTabs = new(@"[ \t]+", RegexOptions.Compiled);

    // Verbatim from TextProcessingUtility.transcriptionPreambleRegex.
    private static readonly Regex Preamble = new(
        @"^\s*[*_#\s]*(?:(?:here|this|below)\s+(?:is|are)\s+)?(?:the\s+)?(?:audio\s+|full\s+|complete\s+)?transcription(?:\s+of\s+[^:\n]{0,80})?\s*:\s*[*_]*\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] PromptPrefixes =
    {
        "convert speech to", "clean text with", "proper punctuation", "transcribe this audio",
        "please transcribe", "transcription:", "audio transcription:", "here is the transcription:",
        "the transcription is:", "transcribed text:", "the audio says:",
    };

    private static readonly string[] PromptSuffixes =
    {
        "with proper punctuation", "clean text with", "keep only the intended meaning",
        "remove filler words", "preserve correct punctuation", "numbers should be written as digits",
    };

    private const string FinalPunctuation = ".!?:;";

    /// <summary>
    /// Normalisation for every model answer (dictation and prompt answers): trim, 3+ newlines to 2,
    /// per line trailing whitespace removed, leading indentation kept, inner runs of spaces/tabs
    /// collapsed, then the "here is the transcription:" style remnants removed.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var trimmed = text.Trim();
        var collapsed = ManyNewlines.Replace(trimmed, "\n\n");
        var lines = collapsed.Split('\n').Select(line =>
        {
            var noTrail = TrailingWhitespace.Replace(line, "");
            var first = -1;
            for (var i = 0; i < noTrail.Length; i++)
            {
                if (!char.IsWhiteSpace(noTrail[i])) { first = i; break; }
            }
            if (first < 0) return noTrail;
            return noTrail[..first] + SpacesAndTabs.Replace(noTrail[first..], " ");
        });
        return CleanRemnants(string.Join("\n", lines));
    }

    /// <summary>
    /// Dictation result: <see cref="Normalize"/>, then first letter capitalised and a final "."
    /// unless the text already ends in one of . ! ? : ; (ClipboardManager.formatTranscription).
    /// Never use this for Dictate Prompt answers.
    /// </summary>
    public static string ForDictation(string? text)
    {
        var formatted = Normalize(text).Trim();
        if (formatted.Length == 0) return "";
        var first = formatted.EnumerateRunes().First();
        var upper = first.ToString().ToUpperInvariant();
        formatted = upper + formatted[first.Utf16SequenceLength..];
        if (FinalPunctuation.IndexOf(formatted[^1]) < 0) formatted += ".";
        return formatted;
    }

    /// <summary>
    /// Confabulation gate: "" when the text is longer than audioSeconds * 60 + 40 characters
    /// (60 chars/s is the Mac app's ceiling). Unchanged when the duration is unknown (&lt;= 0).
    /// </summary>
    public static string DiscardImplausible(string text, double audioSeconds)
    {
        if (audioSeconds <= 0) return text;
        var max = (int)(audioSeconds * 60.0) + 40;
        return text.Length > max ? "" : text;
    }

    private static readonly Regex HeadingLine = new(@"^\s*#{1,6}\s+(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex StarBullet = new(@"^(\s*)[*+] ", RegexOptions.Compiled);

    /// <summary>
    /// Removes Markdown the model adds although the reply goes into a plain text field (lemniscus,
    /// Word), where it would show as stray characters. Headings become "Text:", bold markers vanish,
    /// "* " and "+ " bullets become "- ", code fences are dropped. "- " bullets, single asterisks
    /// inside words, "2*3" and URLs stay as they are.
    /// </summary>
    public static string StripMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            var heading = HeadingLine.Match(line);
            if (heading.Success)
            {
                line = heading.Groups[1].Value;
                if (line.Length > 0 && !line.EndsWith(':')) line += ":";
            }
            line = Bold.Replace(line, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            line = StarBullet.Replace(line, "$1- ");
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    private static string CleanRemnants(string text)
    {
        var cleaned = StripPreamble(text);
        var lower = cleaned.ToLowerInvariant();

        foreach (var prefix in PromptPrefixes)
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var atBoundary = cleaned.Length == prefix.Length
                || char.IsWhiteSpace(cleaned[prefix.Length])
                || prefix.EndsWith(':') || prefix.EndsWith('.');
            if (atBoundary)
            {
                cleaned = cleaned[prefix.Length..];
                break;
            }
        }

        // Like the Swift code, the suffix check runs on the lowercased text from before the prefix removal.
        foreach (var suffix in PromptSuffixes)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                cleaned = cleaned.Length >= suffix.Length ? cleaned[..^suffix.Length] : "";
                break;
            }
        }

        return cleaned.Trim();
    }

    private static string StripPreamble(string text)
    {
        var m = Preamble.Match(text);
        if (!m.Success || m.Length == 0) return text;
        var result = text[(m.Index + m.Length)..].Trim();
        // Never turn a pure preamble into "": the caller should see the original.
        return result.Length == 0 ? text : result;
    }
}
