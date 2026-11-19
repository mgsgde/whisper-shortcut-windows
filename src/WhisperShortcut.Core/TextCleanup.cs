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
    /// Longest clip for which a single-phrase transcript is treated as a silence hallucination.
    /// A clip of 2 s or more that transcribes to just "You" or "Vielen Dank" is kept. Unknown
    /// duration keeps the transcript. Port of TextProcessingUtility.hallucinationMaxClipSeconds (Mac #367).
    /// </summary>
    public const double HallucinationMaxClipSeconds = 2.0;

    /// <summary>
    /// Subtitle filler a model emits when it hears no speech. Closed set, matched against the whole
    /// transcript only. Port of TextProcessingUtility.knownWhisperHallucinations (Mac #367).
    /// The Whisper VAD gate and the settings toggle are not ported.
    /// </summary>
    public static readonly HashSet<string> KnownSilenceHallucinations = new(StringComparer.Ordinal)
    {
        "thank you",
        "thanks for watching",
        "you",
        "bye",
        "vielen dank",
        "untertitel der amara.org-community",
        "subtitles by the amara.org community",
        "please subscribe",
        "bitte abonnieren",
    };

    /// <summary>
    /// Comparison form for the hallucination check: lowercased, trimmed, trailing punctuation and
    /// whitespace removed. "Thank you." and "thank you" match; "Thank you, Dr. Meyer." does not.
    /// </summary>
    public static string PhantomComparisonForm(string text)
    {
        var form = (text ?? "").Trim().ToLowerInvariant();
        while (form.Length > 0 && (char.IsPunctuation(form[^1]) || char.IsWhiteSpace(form[^1])))
            form = form[..^1];
        return form;
    }

    /// <summary>True when the whole transcript is one of <see cref="KnownSilenceHallucinations"/>.</summary>
    public static bool IsKnownSilenceHallucination(string text) =>
        KnownSilenceHallucinations.Contains(PhantomComparisonForm(text));

    /// <summary>
    /// Normalisation for every model answer (dictation and prompt answers): trim, 3+ newlines to 2,
    /// per line trailing whitespace removed, leading indentation kept, inner runs of spaces/tabs
    /// collapsed, then the "here is the transcription:" style remnants removed.
    /// </summary>
    public static string Normalize(string? text)
    {
        var normalized = NormalizeWhitespace(text);
        return normalized.Length == 0 ? "" : CleanRemnants(normalized);
    }

    /// <summary>
    /// Cleanup for Dictate Prompt, quick action and template replies: a leaked
    /// <c>start_thought</c> … <c>end_thought</c> block is removed first (Mac #443,
    /// <see cref="StripLeakedThought"/>), then the same whitespace steps as <see cref="Normalize"/>
    /// plus the anchored "here is the transcription:" preamble strip, but none of the transcript leak
    /// heuristics (literal prompt prefixes and suffixes), which would cut real answers.
    /// Port of the Mac's PromptReplyCleanup.normalize after LeakedThoughtStrip.
    /// </summary>
    public static string NormalizeReply(string? text)
    {
        var normalized = NormalizeWhitespace(StripLeakedThought(text));
        return normalized.Length == 0 ? "" : StripPreamble(normalized).Trim();
    }

    /// <summary>
    /// Drops a leaked Gemini reasoning block (<c>start_thought</c> … <c>end_thought</c>), content
    /// included. An <c>end_thought</c> with no opener drops the prefix; an unclosed
    /// <c>start_thought</c> drops the tail (still streaming, or truncated). Leading spaces and
    /// line breaks (LF or CRLF) are trimmed. Port of LeakedThoughtStrip.strip (Mac #443). Thought parts are already
    /// skipped where the response is parsed; this catches the marker text inside a visible part.
    /// </summary>
    public static string StripLeakedThought(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = text;
        const string startMarker = "start_thought";
        const string endMarker = "end_thought";
        while (true)
        {
            var start = cleaned.IndexOf(startMarker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = cleaned.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
                if (end >= 0)
                {
                    cleaned = string.Concat(cleaned.AsSpan(0, start), cleaned.AsSpan(end + endMarker.Length));
                }
                else
                {
                    cleaned = cleaned[..start];
                    // Drop the spaces that sat before the removed tail (newlines stay).
                    while (cleaned.EndsWith(' ')) cleaned = cleaned[..^1];
                    continue;
                }
            }
            else
            {
                var end = cleaned.IndexOf(endMarker, StringComparison.Ordinal);
                if (end >= 0)
                    cleaned = cleaned[(end + endMarker.Length)..];
                else
                    break;
            }
        }
        // The markers normally sit at the very start; drop the whitespace they leave behind there.
        while (cleaned.Length > 0 && (cleaned[0] == ' ' || cleaned[0] == '\r' || cleaned[0] == '\n'))
            cleaned = cleaned[1..];
        return cleaned;
    }

    /// <summary>
    /// English planning labels. A line matches only when the label is followed by ":".
    /// Port of LeakedPlanningStrip.labels (Mac #457).
    /// </summary>
    private static readonly string[] PlanningLabels =
    {
        "Notice from the audio",
        "User voice style",
        "Voice style",
        "Check prompt constraints",
        "Check constraints",
        "Constraints",
        "Formatting",
        "Details",
        "Content",
        "Plan",
        "Draft",
        "Instruction",
        "Selected text",
        "Task",
    };

    private const string GluePunctuation = ".!?:)";

    /// <summary>
    /// True when the first non-empty line is an English planning header.
    /// "The user " is case-sensitive. Labels are case-insensitive and may have text after the colon.
    /// </summary>
    public static bool LooksLikePlanning(string text)
    {
        var line = FirstNonEmptyTrimmedLine(text);
        if (line == null) return false;
        if (line.StartsWith("The user ", StringComparison.Ordinal)) return true;
        return IsPlanningLabelLine(line);
    }

    /// <summary>
    /// Drops a leaked planning block from non-thought text parts, in order.
    /// Separate planning parts are dropped whole. A plan glued to the answer is cut at the glue
    /// point. Text that cannot be separated is returned unchanged. Port of LeakedPlanningStrip.strip (Mac #457).
    /// </summary>
    public static string StripLeakedPlanning(IReadOnlyList<string> parts)
    {
        var joined = string.Concat(parts);
        if (parts.Count >= 2 && LooksLikePlanning(parts[0]))
        {
            var start = -1;
            for (var i = 1; i < parts.Count; i++)
            {
                if (!LooksLikePlanning(parts[i])) { start = i; break; }
            }
            if (start < 0) return joined;
            var kept = string.Concat(parts.Skip(start));
            LogPlanningDrop(joined, kept, parts.Count);
            return kept;
        }
        if (!LooksLikePlanning(joined) || GluedAnswerStart(joined) is not int cut) return joined;
        var answer = joined[cut..];
        LogPlanningDrop(joined, answer, parts.Count);
        return answer;
    }

    private static bool IsPlanningLabelLine(string trimmedLine)
    {
        foreach (var label in PlanningLabels)
        {
            if (trimmedLine.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? FirstNonEmptyTrimmedLine(string text)
    {
        var lineStart = 0;
        while (true)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var trimmed = text[lineStart..lineEnd].Trim();
            if (trimmed.Length > 0) return trimmed;
            if (lineEnd == text.Length) return null;
            lineStart = lineEnd + 1;
        }
    }

    /// <summary>
    /// Start of the last planning header line: a known label line, or the first non-empty line
    /// when it starts with "The user ".
    /// </summary>
    private static int? LastPlanningLineStart(string text)
    {
        int? anchor = null;
        var isFirstNonEmpty = true;
        var lineStart = 0;
        while (true)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var trimmed = text[lineStart..lineEnd].Trim();
            if (trimmed.Length > 0)
            {
                if (IsPlanningLabelLine(trimmed)
                    || (isFirstNonEmpty && trimmed.StartsWith("The user ", StringComparison.Ordinal)))
                    anchor = lineStart;
                isFirstNonEmpty = false;
            }
            if (lineEnd == text.Length) break;
            lineStart = lineEnd + 1;
        }
        return anchor;
    }

    /// <summary>Index of the uppercase letter that starts the answer, where punctuation is glued to it.</summary>
    private static int? GluedAnswerStart(string text)
    {
        var anchor = LastPlanningLineStart(text);
        if (anchor is not int index) return null;
        while (index < text.Length - 1)
        {
            if (GluePunctuation.Contains(text[index]) && char.IsUpper(text[index + 1]))
                return index + 1;
            index++;
        }
        return null;
    }

    private static void LogPlanningDrop(string original, string kept, int parts)
    {
        var dropped = original.Length - kept.Length;
        if (dropped <= 0) return;
        Log.Warn($"PROMPT-LEAK: dropped planning block ({dropped} chars, parts={parts})");
    }

    private static string NormalizeWhitespace(string? text)
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
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Dictation result: <see cref="Normalize"/>, then first letter capitalised and a final "."
    /// unless the text already ends in one of . ! ? : ; (ClipboardManager.formatTranscription). With
    /// <paramref name="addTrailingPeriod"/> off (spoken-only punctuation) no period is added.
    /// Never use this for Dictate Prompt answers.
    /// <paramref name="audioSeconds"/> is the clip length. A transcript that is entirely one of
    /// <see cref="KnownSilenceHallucinations"/> is dropped when the clip is shorter than
    /// <see cref="HallucinationMaxClipSeconds"/> (Mac #367). Unknown or non-positive duration keeps
    /// the text. Windows has no separate Whisper decode, so this runs for every dictation that
    /// passes a duration.
    /// </summary>
    public static string ForDictation(string? text, bool addTrailingPeriod = true, double? audioSeconds = null)
    {
        // Fillers go after the transcript cleanup and before the capital and the period, so a
        // filler-only clip comes back empty and is treated as no speech (Mac #507).
        var formatted = RemovingFillers(Normalize(text));
        if (formatted.Length == 0) return "";
        if (audioSeconds is > 0 and < HallucinationMaxClipSeconds && IsKnownSilenceHallucination(formatted))
        {
            Log.Info($"HALLUCINATION-FILTER: Dropped known silence hallucination ({formatted.Length} chars, {audioSeconds:F1}s)");
            return "";
        }
        var first = formatted.EnumerateRunes().First();
        var upper = first.ToString().ToUpperInvariant();
        formatted = upper + formatted[first.Utf16SequenceLength..];
        if (addTrailingPeriod && FinalPunctuation.IndexOf(formatted[^1]) < 0) formatted += ".";
        return formatted;
    }

    private static readonly Regex FillerSpaceRun = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Drops hesitation fillers ("Mm-hmm", "Ähm", …) as whole words, case-insensitive, plus one
    /// comma, period or semicolon glued on and the spaces around them. The word list is
    /// <see cref="LiveDictation"/>'s, not a second copy. "Um", "ah", "aha", "achso" and "oh" stay.
    /// A filler at the start, or right after ". ", "! ", "? " or a newline, uppercases the next
    /// letter ("Ähm, ich komme" becomes "Ich komme"). A piece of only fillers comes back "".
    /// Port of TextProcessingUtility.removingFillers (Mac #507). Live pieces keep their own pass
    /// and do not capitalise here.
    /// </summary>
    public static string RemovingFillers(string text)
    {
        var matches = LiveDictation.FillerRegex.Matches(text);
        if (matches.Count == 0) return text;

        var rebuilt = new StringBuilder();
        var cursor = 0;
        var capitalizeNext = false;
        foreach (Match match in matches)
        {
            if (cursor < match.Index)
            {
                var gap = text[cursor..match.Index];
                rebuilt.Append(capitalizeNext ? CapitalizingFirstLetter(gap) : gap);
                capitalizeNext = false;
            }
            // The match, including the spaces and one glued mark around the filler, becomes one space.
            rebuilt.Append(' ');
            if (FillerRemovalStartsSentence(text, match.Index, match.Index + match.Length))
                capitalizeNext = true;
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length)
        {
            var gap = text[cursor..];
            rebuilt.Append(capitalizeNext ? CapitalizingFirstLetter(gap) : gap);
        }
        return FillerSpaceRun.Replace(rebuilt.ToString(), " ").Trim(' ', '\t');
    }

    /// <summary>
    /// True when this filler match sits at the start of <paramref name="text"/> (whitespace aside)
    /// or right after ". ", "! ", "? " or a newline. The spaces the filler pattern consumes count as the gap.
    /// </summary>
    private static bool FillerRemovalStartsSentence(string text, int matchStart, int matchEnd)
    {
        var wordStart = matchStart;
        while (wordStart < matchEnd && (text[wordStart] == ' ' || text[wordStart] == '\t'))
            wordStart++;
        if (wordStart == 0) return true;

        var index = wordStart;
        var sawSpace = false;
        while (index > 0)
        {
            var character = text[index - 1];
            if (character is ' ' or '\t')
            {
                sawSpace = true;
                index--;
                continue;
            }
            break;
        }
        if (index == 0) return true;
        var boundary = text[index - 1];
        if (boundary is '\n' or '\r') return true;
        return sawSpace && boundary is '.' or '!' or '?';
    }

    /// <summary>
    /// Uppercases the first letter of <paramref name="gap"/> when a filler just before it opened a sentence.
    /// Leading whitespace, including newlines, stays. A gap that does not start with a letter
    /// (once that whitespace is skipped) is returned unchanged.
    /// </summary>
    private static string CapitalizingFirstLetter(string gap)
    {
        var index = 0;
        while (index < gap.Length && char.IsWhiteSpace(gap[index])) index++;
        if (index >= gap.Length || !char.IsLetter(gap[index])) return gap;
        return string.Concat(gap.AsSpan(0, index), gap[index].ToString().ToUpperInvariant(), gap.AsSpan(index + 1));
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
        // Mac 63ded84b (#261): connectors ("und", "and", ...) are neutral, but at least one real term must remain.
        if (words.Count == 0 || !words.All(w => folded.Contains(w) || EchoConnectors.Contains(w))
            || !words.Any(folded.Contains)) return text;
        // Stricter than Mac: with a connector inside, "Ramipril und Goedde" (two terms) is ordinary speech;
        // only a list of 3+ distinct terms counts as an echo.
        if (words.Any(EchoConnectors.Contains) && DistinctTermCount(words, glossaryTerms) < MinTermsWithConnectors) return text;

        Log.Error($"Discarding glossary-echo transcript ({trimmed.Length} chars from {audioSeconds:F1}s audio, min plausible {minPlausible:F0}, all words are glossary terms)");
        return "";
    }

    /// <summary>
    /// True when every word of the text belongs to a glossary term and at least two different terms
    /// appear, in any order. One spoken term ("Ramipril.") or any other word ("Ramipril und Goedde")
    /// makes it speech. Connector words ("und", "and", "mit", ...) between terms are neutral (Mac 63ded84b,
    /// #261: "Claude Code, MCP und Bachata." is an echo); a text of connectors only is not a list.
    /// </summary>
    public static bool IsPureGlossaryList(string text, IReadOnlyList<string> glossaryTerms)
    {
        var termIndexByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < glossaryTerms.Count; t++)
            foreach (var w in EchoWords(glossaryTerms[t])) termIndexByWord.TryAdd(w, t);

        var distinct = new HashSet<int>();
        foreach (var w in EchoWords(text))
        {
            if (termIndexByWord.TryGetValue(w, out var idx)) { distinct.Add(idx); continue; }
            if (EchoConnectors.Contains(w)) continue;
            return false;
        }
        // Stricter than Mac: a connector in the text raises the bar to 3 distinct terms ("Ramipril und Goedde" is speech).
        var hasConnector = EchoWords(text).Any(EchoConnectors.Contains);
        return distinct.Count >= (hasConnector ? MinTermsWithConnectors : 2);
    }

    private const int MinTermsWithConnectors = 3;

    private static int DistinctTermCount(IEnumerable<string> words, IReadOnlyList<string> glossaryTerms)
    {
        var termIndexByWord = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var t = 0; t < glossaryTerms.Count; t++)
            foreach (var w in EchoWords(glossaryTerms[t])) termIndexByWord.TryAdd(w, t);
        return words.Where(termIndexByWord.ContainsKey).Select(w => termIndexByWord[w]).Distinct().Count();
    }

    /// <summary>
    /// Strips a glossary list the model appended after an otherwise correct dictation (Mac b64a814b, #436).
    /// Only the trailing term run goes. The run walks backward from the last word and takes the longest
    /// glossary term ending there (a multi-word term counts as one); punctuation between the words is
    /// ignored, so "Frau Dr. Läuffer" and sentence dots inside the echo stay in the run. Connector words are
    /// not skipped and end the run ("Das war's. SumUp und lemniscus." stays). The run is removed only when at
    /// least one word precedes it and the last non-blank character before it is a newline or . ! ? …
    /// followed by whitespace (a colon or the dot in "sabaki.dance" is none). Two or more distinct terms are
    /// stripped when the run is glossary-ordered (<see cref="IsGlossaryOrdered"/>: one descent per ten
    /// pairs, so a keywords-order echo with one wrap-around goes); a single term only when the run is exactly
    /// that one term and the head has 5+ words.
    /// </summary>
    public static string StripTrailingGlossaryEcho(string text, IReadOnlyList<string> glossaryTerms)
    {
        if (glossaryTerms.Count == 0 || text.Length == 0) return text;

        var tokens = EchoTokens(text);
        var run = TrailingTermRun(tokens.Select(t => t.Folded).ToList(), glossaryTerms);
        if (run.Count == 0) return text;

        var runWordCount = run.Sum(m => m.WordCount);
        var runStartWord = tokens.Count - runWordCount;
        // A transcript that is only the glossary belongs to the discard gate.
        if (runStartWord <= 0) return text;
        var boundary = GlossaryEchoBoundary(text, tokens[runStartWord].Start);
        if (boundary < 0) return text;

        var distinct = run.Select(m => m.Index).Distinct().Count();
        var singleTermEcho = run.Count == 1
            && runWordCount == EchoWords(glossaryTerms[run[0].Index]).Count()
            && runStartWord >= MinHeadWordsForSingleTermEcho;
        if (!((distinct >= 2 && IsGlossaryOrdered(run.Select(m => m.Index).ToList())) || singleTermEcho))
            return text;

        var headEnd = boundary + 1;
        var tailLength = text[headEnd..].Trim().Length;
        // Dictated content (patient data): log the size only, never the words.
        Log.Warn($"Stripped trailing glossary echo ({tailLength} chars, {runWordCount} word(s), {distinct} distinct terms)");
        return text[..headEnd].TrimEnd();
    }

    private readonly record struct EchoMatch(int Index, int WordCount);

    private readonly record struct EchoToken(string Folded, int Start);

    // Letter/digit words with their start offset in the original text. Punctuation is not a token.
    // A combining mark inside a word stays part of it (decomposed umlaut); the word is folded after NFC.
    private static List<EchoToken> EchoTokens(string text)
    {
        var tokens = new List<EchoToken>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            var start = i;
            i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i])
                   || char.GetUnicodeCategory(text[i]) == System.Globalization.UnicodeCategory.NonSpacingMark)) i++;
            tokens.Add(new EchoToken(GlossaryPrompt.Fold(text[start..i].Normalize(NormalizationForm.FormC)), start));
        }
        return tokens;
    }

    // Folded term words -> first glossary index; the first entry wins when two terms fold alike.
    private static (Dictionary<string, int> ByWords, int MaxWords) GlossaryTermLookup(IReadOnlyList<string> glossaryTerms)
    {
        var byWords = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxWords = 0;
        for (var t = 0; t < glossaryTerms.Count; t++)
        {
            var words = EchoWords(glossaryTerms[t]).ToList();
            if (words.Count == 0) continue;
            byWords.TryAdd(string.Join(' ', words), t);
            maxWords = Math.Max(maxWords, words.Count);
        }
        return (byWords, maxWords);
    }

    // Longest glossary term ending at word `end` (inclusive).
    private static EchoMatch? LongestTermEndingAt(int end, IReadOnlyList<string> words, Dictionary<string, int> lookup, int maxWords)
    {
        EchoMatch? best = null;
        for (var count = 1; count <= Math.Min(maxWords, end + 1); count++)
            if (lookup.TryGetValue(string.Join(' ', words.Skip(end - count + 1).Take(count)), out var idx))
                best = new EchoMatch(idx, count);
        return best;
    }

    // Suffix of longest terms ending at the last word; stops at the first word that ends no term
    // (connectors are not skipped). Indices in text order.
    private static List<EchoMatch> TrailingTermRun(IReadOnlyList<string> words, IReadOnlyList<string> glossaryTerms)
    {
        var matches = new List<EchoMatch>();
        var (lookup, maxWords) = GlossaryTermLookup(glossaryTerms);
        if (words.Count == 0 || maxWords == 0) return matches;
        var end = words.Count - 1;
        while (end >= 0)
        {
            var match = LongestTermEndingAt(end, words, lookup, maxWords);
            if (match is null) break;
            matches.Add(match.Value);
            end -= match.Value.WordCount;
        }
        matches.Reverse();
        return matches;
    }

    // Glossary-ordered: descents (next index lower than the previous) at most one per ten adjacent pairs.
    private static bool IsGlossaryOrdered(IReadOnlyList<int> termIndices)
    {
        var pairs = termIndices.Count - 1;
        if (pairs <= 0) return true;
        var descents = 0;
        for (var i = 1; i < termIndices.Count; i++)
            if (termIndices[i] < termIndices[i - 1]) descents++;
        return descents <= pairs / 10;
    }

    // Offset of the boundary character before the run: a newline, or . ! ? … followed by whitespace.
    // Spaces and tabs in between are skipped; -1 when there is none.
    private static int GlossaryEchoBoundary(string text, int runStart)
    {
        for (var i = runStart - 1; i >= 0; i--)
        {
            var c = text[i];
            if (IsNewline(c)) return i;
            if (char.IsWhiteSpace(c)) continue;
            return ".!?…".Contains(c) && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]) ? i : -1;
        }
        return -1;
    }

    // Mac 34bb2ce4: minimum head words before a lone trailing glossary term is stripped.
    private const int MinHeadWordsForSingleTermEcho = 5;

    // Mac 63ded84b (#261): filler glued between echoed terms; already folded like GlossaryPrompt.Fold.
    private static readonly HashSet<string> EchoConnectors = new(
        new[] { "und", "and", "mit", "with", "oder", "or", "sowie", "plus", "y", "et" }
            .Select(GlossaryPrompt.Fold), StringComparer.Ordinal);

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

    /// <summary>
    /// Replaces Cyrillic letters that look like Latin ones, only inside a word that already
    /// contains an ASCII Latin letter (A-Z / a-z). A word made only of Cyrillic stays as written.
    /// Umlauts and ß count as letters but do not trigger the fold by themselves.
    /// Port of TextProcessingUtility.foldingCyrillicHomoglyphs (Mac #508).
    /// </summary>
    public static (string Text, int Folded) FoldCyrillicHomoglyphs(string text)
    {
        if (string.IsNullOrEmpty(text)) return ("", 0);

        var output = new StringBuilder(text.Length);
        var folded = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (char.IsLetter(text[index]))
            {
                var wordStart = index;
                var wordEnd = index + 1;
                while (wordEnd < text.Length && char.IsLetter(text[wordEnd])) wordEnd++;
                var hasAscii = false;
                for (var i = wordStart; i < wordEnd; i++)
                {
                    if (IsAsciiLatinLetter(text[i])) { hasAscii = true; break; }
                }
                if (hasAscii)
                {
                    for (var i = wordStart; i < wordEnd; i++)
                    {
                        if (LatinLookAlike(text[i]) is char latin)
                        {
                            output.Append(latin);
                            folded++;
                        }
                        else output.Append(text[i]);
                    }
                }
                else output.Append(text, wordStart, wordEnd - wordStart);
                index = wordEnd;
            }
            else
            {
                output.Append(text[index]);
                index++;
            }
        }

        return folded > 0 ? (output.ToString(), folded) : (text, 0);
    }

    /// <summary>A-Z / a-z only. A precomposed umlaut is not a trigger.</summary>
    private static bool IsAsciiLatinLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>
    /// Cyrillic letters that are visually identical to a Latin letter. Letters absent from this
    /// table (including lowercase в) are never rewritten.
    /// </summary>
    private static char? LatinLookAlike(char character) => character switch
    {
        '\u0430' => 'a', // а
        '\u0435' => 'e', // е
        '\u043E' => 'o', // о
        '\u0440' => 'p', // р
        '\u0441' => 'c', // с
        '\u0443' => 'y', // у
        '\u0445' => 'x', // х
        '\u0456' => 'i', // і
        '\u0458' => 'j', // ј
        '\u0455' => 's', // ѕ
        '\u0410' => 'A', // А
        '\u0412' => 'B', // В
        '\u0415' => 'E', // Е
        '\u041A' => 'K', // К
        '\u041C' => 'M', // М
        '\u041D' => 'H', // Н
        '\u041E' => 'O', // О
        '\u0420' => 'P', // Р
        '\u0421' => 'C', // С
        '\u0422' => 'T', // Т
        '\u0425' => 'X', // Х
        '\u0406' => 'I', // І
        '\u0408' => 'J', // Ј
        '\u0405' => 'S', // Ѕ
        _ => null,
    };
}
