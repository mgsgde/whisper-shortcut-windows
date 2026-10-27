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

    /// <summary>
    /// Lower bound of the plausibility gate: "" when the text is implausibly short for the recording
    /// (under 3 chars/s) and every word in it is a glossary term (the model emitted vocabulary instead
    /// of nothing, e.g. "sabaki.dance" for a silent tail). Unchanged otherwise, and when the duration
    /// or the glossary is unknown.
    /// </summary>
    public static string DiscardGlossaryEcho(string text, double audioSeconds, IReadOnlyList<string> glossaryTerms)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || glossaryTerms.Count == 0) return text;

        // Two or more different terms and nothing else is a list read off the prompt, however long
        // the recording ("Goedde ramipril." for 2 s of silence passes the length floor below).
        if (IsPureGlossaryList(trimmed, glossaryTerms))
        {
            Log.Error($"Discarding glossary-echo transcript ({trimmed.Length} chars, a list of glossary terms only)");
            return "";
        }

        if (audioSeconds <= 0) return text;

        // Real speech runs 10-20 chars/s; 3 chars/s only trips on near-empty output.
        var minPlausible = audioSeconds * 3.0;
        if (!(trimmed.Length < minPlausible)) return text;

        var folded = new HashSet<string>(glossaryTerms.SelectMany(EchoWords), StringComparer.Ordinal);
        var words = EchoWords(trimmed).ToList();
        if (words.Count == 0 || !words.All(folded.Contains)) return text;

        Log.Error($"Discarding glossary-echo transcript ({trimmed.Length} chars from {audioSeconds:F1}s audio, min plausible {minPlausible:F0}, all words are glossary terms)");
        return "";
    }

    /// <summary>
    /// True when every word of the text belongs to a glossary term and at least two different terms
    /// appear, in any order. One spoken term ("Ramipril.") or any other word ("Ramipril und Goedde")
    /// makes it speech.
    /// </summary>
    public static bool IsPureGlossaryList(string text, IReadOnlyList<string> glossaryTerms)
    {
        var termIndexByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < glossaryTerms.Count; t++)
            foreach (var w in EchoWords(glossaryTerms[t])) termIndexByWord.TryAdd(w, t);

        var distinct = new HashSet<int>();
        foreach (var w in EchoWords(text))
        {
            if (!termIndexByWord.TryGetValue(w, out var idx)) return false;
            distinct.Add(idx);
        }
        return distinct.Count >= 2;
    }

    /// <summary>
    /// Strips a glossary list the model appended after an otherwise correct dictation. Only the tail goes,
    /// and only when it starts after a real sentence boundary (newline, or . ! ? … followed by whitespace;
    /// a colon or the dot in "sabaki.dance" is none), the head contains a letter or digit, every tail word
    /// is a glossary word, the words' term indices never decrease, and at least two distinct terms appear.
    /// </summary>
    public static string StripTrailingGlossaryEcho(string text, IReadOnlyList<string> glossaryTerms)
    {
        if (glossaryTerms.Count == 0 || text.Length == 0) return text;

        var bodyEnd = text.Length;
        while (bodyEnd > 0 && char.IsWhiteSpace(text[bodyEnd - 1])) bodyEnd--;

        // Closing punctuation of the transcript itself (and a glued quote) is not the boundary.
        const string terminal = ".!?…\"'”’“»›";
        var searchEnd = bodyEnd;
        while (searchEnd > 0 && terminal.Contains(text[searchEnd - 1])) searchEnd--;

        const string boundaryPunct = ".!?…";
        var boundary = -1;
        for (var i = 0; i < searchEnd; i++)
        {
            var c = text[i];
            if (IsNewline(c)) boundary = i;
            else if (boundaryPunct.Contains(c) && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) boundary = i;
        }
        if (boundary < 0) return text;

        var headEnd = boundary + 1;
        var head = text[..headEnd];
        if (!head.Any(char.IsLetterOrDigit)) return text;

        // First glossary entry containing the word; later duplicates do not reorder the echo.
        var termIndexByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < glossaryTerms.Count; t++)
            foreach (var w in EchoWords(glossaryTerms[t])) termIndexByWord.TryAdd(w, t);

        var tail = bodyEnd > headEnd ? text[headEnd..bodyEnd] : "";
        var distinct = new HashSet<int>();
        var last = -1;
        foreach (var w in EchoWords(tail))
        {
            if (!termIndexByWord.TryGetValue(w, out var idx)) return text;
            if (last >= 0 && idx < last) return text;
            last = idx;
            distinct.Add(idx);
        }
        if (distinct.Count < 2) return text;

        Log.Warn($"Stripped trailing glossary echo ({tail.Trim().Length} chars, {distinct.Count} distinct terms)");
        return head.TrimEnd();
    }

    private static readonly string[] RefusalPhrases =
    {
        "geben sie mir die audiodatei", "gib mir die audiodatei", "den ich transkribieren soll",
        "die ich transkribieren soll", "text, den ich transkribieren", "please provide the audio",
        "provide the audio file", "provide the text you", "i can transcribe",
    };

    private static readonly string[] PromptLeakKeywords =
    {
        "convert speech to", "clean text with", "proper punctuation", "transcribe this audio",
        "remove filler words", "disfluencies",
    };

    private static readonly string[] SystemPatterns =
    {
        "here is the transcription", "transcription:", "audio transcription:", "transcribed text:",
        "the audio says:", "the transcription is:",
    };

    /// <summary>
    /// True when a transcription answer is not a transcript: a chatbot refusal / request for the audio
    /// (only when the whole answer is short, under 160 chars, or exactly the phrase), or a leak of the
    /// instruction (the full default prompt, more than 2 prompt keywords, a "context:" prefix, or a
    /// "transcription:"-style header as whole text or prefix). Port of TextProcessingUtility.validateSpeechText.
    /// Callers treat true as "no speech detected" and must not paste the text.
    /// </summary>
    public static bool LooksLikeRefusal(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return false;
        var lower = trimmed.ToLowerInvariant();

        // Substring matches over a long real transcript false-positive: only short answers count.
        var isShort = trimmed.Length < 160;
        if (RefusalPhrases.Any(p => lower == p || (isShort && lower.Contains(p, StringComparison.Ordinal))))
        {
            Log.Info($"PROMPT-DETECTION: assistant-mode refusal in transcription ({trimmed.Length} chars)");
            return true;
        }

        if (trimmed.Contains(Prompts.DefaultTranscriptionSystemPrompt.Trim(), StringComparison.Ordinal))
        {
            Log.Info($"PROMPT-DETECTION: full prompt in transcription ({trimmed.Length} chars)");
            return true;
        }

        var keywordCount = PromptLeakKeywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
        if (keywordCount > 2)
        {
            Log.Info($"PROMPT-DETECTION: prompt leak in transcription, {keywordCount} keywords");
            return true;
        }

        if (trimmed.StartsWith("context:", StringComparison.Ordinal))
        {
            Log.Info("PROMPT-DETECTION: context prefix in transcription");
            return true;
        }

        if (SystemPatterns.Any(p => lower == p || lower.StartsWith(p + " ", StringComparison.Ordinal) || lower.StartsWith(p + "\n", StringComparison.Ordinal)))
        {
            Log.Info("PROMPT-DETECTION: system pattern in transcription");
            return true;
        }
        return false;
    }

    private static bool IsNewline(char c) => c is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029';

    // Words split on anything that is not a letter or digit, folded like the glossary parse.
    // NFC first so a decomposed "o" + umlaut stays one letter.
    private static IEnumerable<string> EchoWords(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { yield return GlossaryPrompt.Fold(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) yield return GlossaryPrompt.Fold(sb.ToString());
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
