using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Decides whether a Dictate Prompt reply may be pasted at all. Deliberately conservative: it only
/// catches replies that cannot be what the user asked for, so a long compose reply or a reply that merely
/// mentions "voice instruction" in a sentence passes. Port of the Mac's PromptReplyGuard (Mac #459).
/// </summary>
public static class PromptReplyGuard
{
    public enum Verdict
    {
        Ok,
        /// <summary>The reply quotes one of the app's own request labels at the start of a line.</summary>
        ScaffoldEcho,
        /// <summary>The reply is far longer than anything the selection and instruction could call for.</summary>
        Runaway,
    }

    /// <summary>A reply longer than max(<see cref="RunawayMinChars"/>, <see cref="RunawayFactor"/> x (selection + instruction)) is not pasted.</summary>
    public const int RunawayMinChars = 2000;
    public const int RunawayFactor = 8;

    /// <summary>
    /// <paramref name="instruction"/> is null when the spoken instruction is unknown; the length rule needs it
    /// and is skipped, the scaffold rule does not.
    /// </summary>
    public static Verdict Check(string reply, string? selectedText, string? instruction)
    {
        // A label the user's own text already has at a line start (editing a document about this app) is no echo.
        if (StartsLineWithScaffoldLabel(reply)
            && !(selectedText != null && StartsLineWithScaffoldLabel(selectedText))
            && !(instruction != null && StartsLineWithScaffoldLabel(instruction)))
            return Verdict.ScaffoldEcho;
        if (instruction != null)
        {
            var inputLength = (selectedText?.Length ?? 0) + instruction.Length;
            var cap = Math.Max(RunawayMinChars, RunawayFactor * inputLength);
            if (reply.Length > cap) return Verdict.Runaway;
        }
        return Verdict.Ok;
    }

    public static bool StartsLineWithScaffoldLabel(string text)
    {
        foreach (var line in text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart(' ', '\t');
            if (trimmed.StartsWith(Prompts.ClipboardSelectionLabel, StringComparison.Ordinal)
                || trimmed.StartsWith(Prompts.VoiceInstructionLabel, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static readonly char[] LineBreaks = { (char)10, (char)13, (char)0x85, (char)0x2028, (char)0x2029 };
}

/// <summary>
/// Checks on a transcribed voice instruction, before the model is called. Typed instructions and saved
/// prompts never pass through here. Port of the Mac's PromptInstructionGuard (Mac #459).
/// </summary>
public static class PromptInstructionGuard
{
    /// <summary>Fewer letters than this is a mis-transcription ("0", "Oh.").</summary>
    public const int MinLetters = 3;
    /// <summary>A transcription of at most this many words may snap to a saved quick prompt.</summary>
    public const int SnapMaxWords = 5;
    public const double SnapMinSimilarity = 0.8;

    private static readonly Regex TimestampStart = new(@"^\s*\d{2}:\d{2}:\d{2}[.,]\d{3}", RegexOptions.CultureInvariant);

    /// <summary>Too little language to be an instruction, or an SRT/VTT-style timestamp where speech should be.</summary>
    public static bool IsNotUnderstood(string instruction)
    {
        // Scripts without word spacing carry a whole instruction in one or two characters ("缩短").
        if (!HasUnspacedScript(instruction))
        {
            var letters = instruction.Count(char.IsLetter);
            if (letters < MinLetters) return true;
        }
        return TimestampStart.IsMatch(instruction);
    }

    private static bool HasUnspacedScript(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            if (IsUnspacedScript(cp)) return true;
        }
        return false;
    }

    /// <summary>CJK ideograph, kana or hangul.</summary>
    private static bool IsUnspacedScript(int v) =>
        v is (>= 0x3040 and <= 0x30FF) or (>= 0x31F0 and <= 0x31FF)
          or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F)
          or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xAC00 and <= 0xD7AF);

    /// <summary>
    /// The saved prompt a short transcription is a near-miss of, or null. Only when exactly one saved prompt
    /// is at least <see cref="SnapMinSimilarity"/> close: zero or several change nothing.
    /// </summary>
    public static string? Snapped(string instruction, IEnumerable<string> savedPrompts)
    {
        if (instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > SnapMaxWords) return null;
        var key = NormalizedKey(instruction);
        if (key.Length == 0) return null;
        var matches = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in savedPrompts)
        {
            var text = raw.Trim();
            var candidateKey = NormalizedKey(text);
            if (candidateKey.Length == 0 || !seen.Add(candidateKey)) continue;
            if (Similarity(key, candidateKey) >= SnapMinSimilarity - 1e-9) matches.Add(text);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Lowercased, punctuation dropped, whitespace collapsed.</summary>
    public static string NormalizedKey(string text)
    {
        var kept = new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        return string.Join(" ", kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>1 - editDistance / longerLength, 1 for equal strings.</summary>
    public static double Similarity(string a, string b)
    {
        var longer = Math.Max(a.Length, b.Length);
        if (longer == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var row = new int[b.Length + 1];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
                row[j] = Math.Min(Math.Min(previous[j] + 1, row[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = row;
        }
        return 1 - (double)previous[b.Length] / longer;
    }
}

/// <summary>The model's reply was withheld by <see cref="PromptReplyGuard"/>. Retryable: the recording is kept.</summary>
public sealed class PromptReplyRejectedException()
    : Exception("Die Antwort des Modells sah fehlerhaft aus, daher wurde nichts eingefügt und dein Text ist unverändert. Deine Aufnahme ist gespeichert, versuche „Erneut versuchen“ oder ein anderes Modell.");

/// <summary>The transcribed instruction was a mis-transcription (<see cref="PromptInstructionGuard"/>). Not retryable: the same audio gives the same transcript.</summary>
public sealed class InstructionNotUnderstoodException()
    : Exception("Deine gesprochene Anweisung wurde nicht verstanden, daher wurde nichts geändert. Bitte sag sie noch einmal.");
