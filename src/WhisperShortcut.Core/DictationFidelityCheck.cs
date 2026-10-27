using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Flags content words in a Dictate Prompt output that the dictation itself does not contain.
///
/// In a medical-practice setting the local LLM reorders a dictated note into sections
/// (Anamnese / Befund / Therapie). The risk is not bad wording but invented content, for example a
/// Therapie section that the model fills with plausible treatments nobody dictated. This check is a
/// deterministic word comparison between output and sources. It never edits the text; the caller
/// only shows the returned words as a warning.
///
/// It catches additions only. A removed negation ("kein Schwindel" becoming "Schwindel") is an
/// omission and not looked for. An added negation ("Schwindel" becoming "keinen Schwindel") flips the
/// meaning, so it is flagged unless some source contains a negation itself.
/// </summary>
public static class DictationFidelityCheck
{
    // Markers the prompt itself tells the model to write. They are not content, so they go first.
    private static readonly string[] Markers = { "(nicht diktiert)", "[?]", "[Zuordnung prüfen]" };

    // Section headings that may appear anywhere. "diagnose" is missing on purpose: an invented
    // diagnosis has to be flagged.
    private static readonly HashSet<string> HeadingWords = new() { "anamnese", "befund", "therapie", "verlauf" };

    private static readonly Regex NewlineSplit = new(@"\r\n|[\n\r\u0085\u2028\u2029]", RegexOptions.Compiled);

    // Dictation says "fünf", the output writes "5" (and the other way round). Keys are already folded.
    private static readonly Dictionary<string, string> NumberWords = new()
    {
        ["null"] = "0", ["eins"] = "1", ["zwei"] = "2", ["drei"] = "3", ["vier"] = "4", ["funf"] = "5",
        ["sechs"] = "6", ["sieben"] = "7", ["acht"] = "8", ["neun"] = "9", ["zehn"] = "10", ["elf"] = "11",
        ["zwolf"] = "12", ["dreizehn"] = "13", ["vierzehn"] = "14", ["funfzehn"] = "15",
        ["sechzehn"] = "16", ["siebzehn"] = "17", ["achtzehn"] = "18", ["neunzehn"] = "19",
        ["zwanzig"] = "20", ["dreissig"] = "30", ["vierzig"] = "40", ["funfzig"] = "50",
        ["sechzig"] = "60", ["siebzig"] = "70", ["achtzig"] = "80", ["neunzig"] = "90", ["hundert"] = "100",
        ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5",
        ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11",
        ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14", ["fifteen"] = "15",
        ["sixteen"] = "16", ["seventeen"] = "17", ["eighteen"] = "18", ["nineteen"] = "19",
        ["twenty"] = "20",
    };

    // Inflection suffixes, longest first, so "Schmerzen" matches "Schmerz" and "kraniosakraler"
    // matches "kraniosakrale".
    private static readonly string[] Suffixes = { "ern", "em", "en", "er", "es", "e", "n", "s" };

    // Words that make a heading line a heading even though they are not content: besides the
    // section words, the generic labels a note may carry. Only used to judge whole lines.
    private static readonly HashSet<string> HeadingLineWords = new(HeadingWords)
    {
        "diagnose", "diagnosen", "beurteilung", "prozedere", "weitere", "hinweis", "hinweise",
        "zusammenfassung", "sonstiges", "notizen", "aufgaben",
    };

    // Negations. Not in the stopwords: one in the output that no source backs is an added negation.
    private static readonly HashSet<string> Negations = new() { "nicht", "kein", "keine", "keinen", "keiner" };

    // Function words, German and English. Folded form (no umlauts).
    private static readonly HashSet<string> Stopwords = new()
    {
        "und", "oder", "aber", "der", "die", "das", "des", "den", "dem", "ein", "eine", "einer",
        "eines", "einem", "einen", "mit", "im", "in", "am", "an", "auf", "bei", "bis", "zu", "zur",
        "zum", "von", "vom", "seit", "nach", "vor", "uber", "unter", "aus", "fur", "ohne", "gegen",
        "durch", "als", "wie", "so", "auch", "noch", "nur", "sehr", "ist", "sind", "war", "wird",
        "werden", "wurde", "hat", "haben", "er", "sie", "es", "ich", "wir", "ihr", "man", "sich",
        "the", "a", "and", "or", "of", "to", "on", "at", "for", "with", "is", "are", "was", "be",
        "it", "this", "that",
    };

    /// <summary>
    /// Content words of <paramref name="output"/> with no counterpart in any of <paramref name="sources"/>
    /// (the transcript, plus the selected text when there is one). Deduplicated by normalized form, in
    /// order of first occurrence, original spelling from <paramref name="output"/>. Null or blank
    /// sources are ignored.
    /// </summary>
    public static IReadOnlyList<string> UnsupportedWords(string output, IEnumerable<string?> sources)
    {
        var sourceForms = new HashSet<string>();
        var sourceStems = new HashSet<string>();
        var compounds = new HashSet<string>();
        var compoundStems = new HashSet<string>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var list = Tokens(source).Select(Normalize).ToList();
            foreach (var form in list)
            {
                sourceForms.Add(form);
                sourceStems.Add(Stem(form));
            }
            // A dictated "Lenden Wirbelsäule" comes back as "Lendenwirbelsäule".
            for (var i = 0; i + 1 < list.Count; i++)
            {
                var joined = list[i] + list[i + 1];
                compounds.Add(joined);
                compoundStems.Add(Stem(joined));
            }
        }

        // "nicht" for a dictated "kein" is a rewording, so any source negation backs any output negation.
        var sourceHasNegation = sourceForms.Overlaps(Negations);

        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var token in OutputContentTokens(output, sourceForms, sourceStems))
        {
            var form = Normalize(token);
            var supported = (sourceHasNegation && Negations.Contains(form))
                || sourceForms.Contains(form)
                || sourceStems.Contains(Stem(form))
                || compounds.Contains(form)
                || compoundStems.Contains(Stem(form));
            if (supported || !seen.Add(form)) continue;
            result.Add(token);
        }
        return result;
    }

    // Original-spelling tokens of the output that count as content words: no markers, no heading
    // lines, no heading words, no stopwords, no short noise.
    private static List<string> OutputContentTokens(string output, HashSet<string> sourceForms, HashSet<string> sourceStems)
    {
        var text = output;
        foreach (var marker in Markers)
            text = Regex.Replace(text, Regex.Escape(marker), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new List<string>();
        foreach (var line in NewlineSplit.Split(text))
        {
            if (IsHeadingLine(line, sourceForms, sourceStems)) continue;
            foreach (var token in Tokens(line))
                if (IsContent(token)) result.Add(token);
        }
        return result;
    }

    // A whole line that is a section heading, with or without Markdown decoration:
    // "## Befund", "**Therapie:**", "Weitere Hinweise:". "Befund: Psoas rechts" is not one. Every word
    // must be a heading word, a function word or from the sources: "Verdacht auf Bandscheibenvorfall:"
    // is an invented finding dressed up as a heading and stays checked.
    private static bool IsHeadingLine(string line, HashSet<string> sourceForms, HashSet<string> sourceStems)
    {
        var trimmed = line.Trim(' ', '\t');
        var core = trimmed.Trim('#', '*', '_', ' ');
        if (!trimmed.StartsWith('#'))
        {
            if (!core.EndsWith(':')) return false;
            core = core[..^1];
        }
        var words = Tokens(core);
        if (!trimmed.StartsWith('#') && words.Count is < 1 or > 3) return false;
        foreach (var word in words)
        {
            var form = Normalize(word);
            if (HeadingLineWords.Contains(form) || Stopwords.Contains(form)
                || sourceForms.Contains(form) || sourceStems.Contains(Stem(form))) continue;
            return false;
        }
        return true;
    }

    // Splits on everything that is not a letter or digit, so "L4/L5" and "ISG-Blockade" fall apart
    // into the parts a dictation would have spoken separately. Composed first so umlauts stay whole.
    private static List<string> Tokens(string text)
    {
        var composed = text.Normalize(NormalizationForm.FormC);
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i <= composed.Length; i++)
        {
            var isToken = i < composed.Length && char.IsLetterOrDigit(composed[i]);
            if (isToken) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                result.Add(composed[start..i]);
                start = -1;
            }
        }
        return result;
    }

    // Whether a raw token is worth checking at all.
    private static bool IsContent(string token)
    {
        var form = Normalize(token);
        if (HeadingWords.Contains(form)) return false;
        if (token.Any(char.IsNumber)) return true;
        if (Stopwords.Contains(form)) return false;
        // Short words are noise ("zu", "bd"), but an uppercase "M" is an abbreviation someone added.
        if (token.Length < 3) return token == token.ToUpperInvariant();
        return true;
    }

    // Lowercased, diacritics folded, ß as "ss", and number words as digits.
    private static string Normalize(string token)
    {
        var decomposed = token.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var folded = sb.ToString().Normalize(NormalizationForm.FormC).Replace("ß", "ss");
        return NumberWords.TryGetValue(folded, out var number) ? number : folded;
    }

    // Normalized form without its longest inflection suffix, if at least 4 characters stay.
    private static string Stem(string form)
    {
        foreach (var suffix in Suffixes)
            if (form.EndsWith(suffix, StringComparison.Ordinal) && form.Length - suffix.Length >= 4)
                return form[..^suffix.Length];
        return form;
    }
}
