using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Turns the stored glossary lines into what a Gemini request may show the model, and into the data
/// the transcript gates need. Port of GlossaryDocument.swift plus SpeechService.parsedGlossary /
/// appendGlossaryHint / tieBreakerBlock / correctingRejectedSpellings.
/// A line may carry a note like <c>Bachata (not "Batchata")</c>: the rejected spelling must never be
/// offered as vocabulary, only as a tie-breaker.
/// </summary>
public static class GlossaryPrompt
{
    /// <summary>Longest term that is still vocabulary rather than a phrase (GlossaryDocument.maxTermWords).</summary>
    public const int MaxTermWords = 5;

    /// <summary>One <c>Term (not "Wrong")</c> line: the spelling the user wants plus every rejected one.</summary>
    /// <param name="Correct">The spelling the user asserted.</param>
    /// <param name="Rejected">Spellings the user marked as wrong, in order.</param>
    public sealed record Correction(string Correct, IReadOnlyList<string> Rejected);

    /// <summary>The glossary split into a flat reference list and the annotated pairs.</summary>
    /// <param name="Terms">Reference vocabulary: annotations removed, over-long phrases dropped, rejected spellings filtered out.</param>
    /// <param name="Corrections">The annotated pairs, in glossary order.</param>
    public sealed record Parsed(IReadOnlyList<string> Terms, IReadOnlyList<Correction> Corrections);

    // The one annotation shape (GlossaryDocument.annotationPattern): "(not ...)" / "(nicht ...)".
    private static readonly Regex Annotation = new(@"\((?:not|nicht)\s+[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnnotationInnerPrefix = new(@"^\((?:not|nicht)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnnotationStrip = new(@"\s*\((?:not|nicht)\s+[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "^\s*" so an indented "Terms:" is a label, not a term.
    private static readonly Regex TermsPrefix = new(@"^\s*Terms:\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly char[] TrimSet = { ' ', '"', '\'' };
    private static readonly string[] LineBreaks = { "\r\n", "\n", "\r", "\u0085", "\u2028", "\u2029" };

    /// <summary>
    /// Case- and diacritic-insensitive key ("Gödde" == "godde"). Culture-independent on purpose, like the
    /// Mac's POSIX fold, so the same glossary matches on every machine.
    /// </summary>
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>True when the term is non-empty and at most <see cref="MaxTermWords"/> words.</summary>
    public static bool WithinWordCap(string term) =>
        term.Length > 0 && term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= MaxTermWords;

    /// <summary>Rejected spellings on one line, in order. An annotation with no term beside it still counts.</summary>
    public static IReadOnlyList<string> RejectedSpellings(string line)
    {
        var forms = new List<string>();
        foreach (Match m in Annotation.Matches(line))
        {
            var inner = AnnotationInnerPrefix.Replace(m.Value, "", 1);
            if (inner.EndsWith(')')) inner = inner[..^1];
            foreach (var form in inner.Split(','))
            {
                var cleaned = form.Trim(TrimSet);
                if (cleaned.Length > 0) forms.Add(cleaned);
            }
        }
        return forms;
    }

    /// <summary>The line with annotations and a leading "Terms:" label removed, trimmed.</summary>
    public static string LineTermText(string line) =>
        TermsPrefix.Replace(AnnotationStrip.Replace(line, ""), "").Trim(' ', '\t');

    /// <summary>
    /// Parses the glossary once for all consumers (SpeechService.parsedGlossary). The annotated spelling
    /// wins the fold dedupe over a bare line; rejected spellings are filtered out of the terms by name.
    /// </summary>
    public static Parsed Parse(IReadOnlyList<string> lines)
    {
        var rawLines = SplitLines(lines);

        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var corrections = new List<Correction>();
        foreach (var line in rawLines)
        {
            var lineRejects = RejectedSpellings(line);
            foreach (var form in lineRejects)
            {
                var folded = Fold(form);
                if (folded.Length > 0) rejected.Add(folded);
            }
            if (lineRejects.Count == 0) continue;
            var correct = LineTermText(line);
            if (!WithinWordCap(correct)) continue;
            corrections.Add(new Correction(correct, lineRejects));
        }

        var correctionSpellingByFold = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in corrections) correctionSpellingByFold.TryAdd(Fold(c.Correct), c.Correct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<string>();
        foreach (var line in rawLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var body = TermsPrefix.Replace(AnnotationStrip.Replace(line, ""), "");
            foreach (var raw in body.Split(','))
            {
                var term = raw.Trim(' ', '\t');
                if (term.Length == 0 || !WithinWordCap(term)) continue;
                var folded = Fold(term);
                if (rejected.Contains(folded) || !seen.Add(folded)) continue;
                terms.Add(correctionSpellingByFold.TryGetValue(folded, out var wins) ? wins : term);
            }
        }
        return new Parsed(terms, corrections);
    }

    /// <summary>
    /// The block appended to the transcription instruction ("" when there is nothing to say): the
    /// reference-vocabulary block plus the homophone tie-breakers, wording verbatim from the Mac.
    /// </summary>
    public static string Hint(IReadOnlyList<string> lines)
    {
        var parsed = Parse(lines);
        var glossary = string.Join(", ", parsed.Terms);
        if (glossary.Length == 0 && parsed.Corrections.Count == 0) return "";

        var block = "";
        if (glossary.Length > 0)
        {
            block = Prompts.GlossaryHintHeader + glossary;
        }
        var tie = TieBreakerBlock(parsed.Corrections);
        if (tie != null) block = block.Length == 0 ? tie : block + "\n\n" + tie;

        Log.Info($"GLOSSARY: conditioning transcription with {glossary.Length} chars + {parsed.Corrections.Count} tie-breaker(s)");
        return block;
    }

    /// <summary>The sanitized reference terms, as input for the transcript gates (echo discard / trailing strip).</summary>
    public static IReadOnlyList<string> EchoTerms(IReadOnlyList<string> lines) => Parse(lines).Terms;

    /// <summary>
    /// Fixes a single-word term the model spelled the common way: "Herr Gödde" becomes "Herr Goedde"
    /// for the term "goedde". Matching is whole-word, on the form with ö/oe, ä/ae, ü/ue, ß/ss and case
    /// folded. Gemini keeps the usual spelling of a near-homophone however the hint is worded, so this
    /// is done here. No fuzzy matching: a one-letter difference ("Gerade" vs "Goedde") is another word.
    /// An all-lowercase term takes the capitalization of the word it replaces.
    /// </summary>
    public static string ApplyTermSpellings(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return text;

        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (term.Length == 0 || !term.All(IsLetterOrMark)) continue;
            byKey.TryAdd(TranslitFold(term), term);
        }
        if (byKey.Count == 0) return text;

        var replaced = 0;
        var result = WordToken.Replace(text, m =>
        {
            var word = m.Value;
            if (!byKey.TryGetValue(TranslitFold(word), out var term) || word == term) return word;
            replaced++;
            if (term == term.ToLowerInvariant() && char.IsUpper(word[0]))
                return char.ToUpperInvariant(term[0]) + term[1..];
            return term;
        });
        if (replaced > 0) Log.Info($"Glossary spelling: {replaced} word(s) set to the glossary spelling");
        return result;
    }

    private static readonly Regex WordToken = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    // German transliteration key: "Gödde" and "Goedde" meet. Case-folded, other diacritics stay.
    private static string TranslitFold(string s) =>
        s.Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");

    /// <summary>
    /// Rewrites a transcript that carries a spelling the user rejected (<c>Claude CLI (not "Cloud CLI")</c>).
    /// Only multi-word rejected forms are rewritten, and a match preceded by a capitalised word is left
    /// alone ("Google Cloud CLI" survives, "die Cloud CLI" becomes "die Claude CLI"). Both rules fail
    /// toward changing nothing.
    /// </summary>
    public static string CorrectRejectedSpellings(string text, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var corrections = Parse(lines).Corrections;
        if (corrections.Count == 0) return text;

        var result = text;
        foreach (var c in corrections)
        {
            foreach (var rejected in c.Rejected)
            {
                if (!rejected.Contains(' ')) continue;
                result = ReplacingRejectedSpelling(rejected, c.Correct, result);
            }
        }
        if (!string.Equals(result, text, StringComparison.Ordinal))
            Log.Info("GLOSSARY-CORRECT: rewrote rejected spelling(s) in transcript");
        return result;
    }

    /// <summary>
    /// One rejected spelling replaced on word boundaries, except where a capitalised word precedes it.
    /// Public so tests can pin both guards.
    /// </summary>
    public static string ReplacingRejectedSpelling(string rejected, string correct, string text)
    {
        var regex = new Regex(@"\b" + Regex.Escape(rejected) + @"\b");
        var matches = regex.Matches(text);
        var result = text;
        // Back to front so earlier match offsets stay valid.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            if (IsPrecededByCapitalizedWord(text, m.Index)) continue;
            result = result[..m.Index] + correct + result[(m.Index + m.Length)..];
        }
        return result;
    }

    // True when the token right before `location` starts with an uppercase letter. Punctuation,
    // digits or the start of the text count as "not preceded".
    private static bool IsPrecededByCapitalizedWord(string text, int location)
    {
        var index = location - 1;
        while (index >= 0 && char.IsWhiteSpace(text[index])) index--;
        var wordEnd = index;
        while (index >= 0 && IsLetterOrMark(text[index])) index--;
        if (wordEnd <= index) return false;
        return CharUnicodeInfo.GetUnicodeCategory(text[index + 1]) == UnicodeCategory.UppercaseLetter;
    }

    private static bool IsLetterOrMark(char c)
    {
        if (char.IsLetter(c)) return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static string? TieBreakerBlock(IReadOnlyList<Correction> corrections)
    {
        if (corrections.Count == 0) return null;
        var pairs = string.Join("\n", corrections.Select(c =>
            $"- \"{c.Correct}\" — not \"{string.Join("\" / \"", c.Rejected)}\""));
        return "Homophone tie-breakers — this refines the rule above for these specific pairs. For "
            + "this speaker each pair is settled in favour of the FIRST spelling: when what you hear "
            + "could be either member of a pair, write the first spelling, even though the second is "
            + "the more common word. Use the second spelling only where the surrounding words make it "
            + "unmistakable, such as an established product name it forms part of. These are spelling "
            + "decisions only — never output either form unless you actually heard it.\n" + pairs;
    }

    // A stored line may itself contain newlines (pasted block); the Mac splits the raw text on them.
    private static List<string> SplitLines(IReadOnlyList<string> lines) =>
        lines.SelectMany(l => (l ?? "").Split(LineBreaks, StringSplitOptions.None)).ToList();
}
