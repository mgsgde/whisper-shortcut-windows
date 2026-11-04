using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

public enum PunctuationMode { Automatic, SpokenOnly }

public static class PunctuationModes
{
    /// <summary>"Only when spoken" is an offline-dictation setting; cloud models, meetings and spoken instructions keep the model's punctuation.</summary>
    public static PunctuationMode Effective(PunctuationMode stored, bool isOffline, bool duringMeeting) =>
        isOffline && !duringMeeting ? stored : PunctuationMode.Automatic;
}

/// <summary>
/// Turns spoken layout commands into layout in offline transcripts. Exact port of the Mac app's
/// SpokenPunctuation.swift: Parakeet writes a spoken "Doppelpunkt" or "neuer Absatz" out as words,
/// where cloud models already produce the punctuation.
/// Deliberately narrow: "Punkt", "Komma" and "Fragezeichen" are ordinary German words and stay.
/// The exception is the "Only when spoken" punctuation mode (<see cref="PunctuationMode.SpokenOnly"/>):
/// there the user has opted in to saying the marks, so "Punkt", "Komma", "Fragezeichen",
/// "Ausrufezeichen", "Semikolon", "Strichpunkt", "Schrägstrich" and "drei Pünktchen" are converted too.
/// There the model's own commas and semicolons are removed (<see cref="StripModelPunctuation"/>).
/// Without token timings its guessed "?" and "!" are removed too; with timings (<c>timedMarks</c>)
/// those marks were already kept or dropped by ParakeetPunctuationTiming, so they stay.
/// Its "." and ":" are kept, because the model often writes a spoken "Punkt" as "." itself.
/// </summary>
public static class SpokenPunctuation
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Folded (GlossaryDocument.fold) single words the rules below turn into layout or marks.
    /// Mirrors the spoken words in the rules; a glossary boost must never rewrite one of them.
    /// Keep in sync when a rule gains a word.
    /// </summary>
    public static readonly IReadOnlySet<string> CommandWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "doppelpunkt", "komma", "punkt", "fragezeichen", "ausrufezeichen", "schragstrich",
        "schraegstrich", "schreckstrich", "semikolon", "strichpunkt", "punktchen", "puenktchen",
        "auslassungspunkte", "absatz", "zeile",
    };

    private sealed record Rule(Regex Regex, string Template);

    private static Rule MakeRule(string pattern, string template) => new(new Regex(pattern, Opts), template);

    // Order matters: the colon runs first so "Befund Doppelpunkt neuer Absatz" keeps its colon when
    // the paragraph rule then strips the stray space before the break.
    private static readonly Rule[] Rules =
    {
        // The colon is already there: a model reply that kept the spoken word after its own heading
        // ("Befund: Doppelpunkt Druckschmerz", "Anamnese:\nDoppelpunkt Patientin"). Drop the word only.
        MakeRule(@"(:[ \t]*\n?[ \t]*)\bdoppelpunkt\b[ \t,.;:]*", "$1"),
        // "Anamnese Doppelpunkt Patient" / "Befund, Doppelpunkt. Inspektion" -> "Befund: Inspektion".
        // Eats the punctuation the ASR put around the word on both sides.
        MakeRule(@"[ \t,.;]*\bdoppelpunkt\b[ \t,.;:]*", ": "),
        // Parakeet sometimes glues the command onto the next word ("Befund Doppelpunktdruckschmerz").
        // The following word's first letter is upper-cased afterwards (CapitalizeAfterGluedColon).
        // Inflections of the noun itself ("Doppelpunkte", "-en", "-es", "-s") are left alone.
        MakeRule(@"[ \t,.;]*\bdoppelpunkt(?!(?:e|en|es|s)\b)(?=\p{L})", ": \u0001"),
        // A comma before a spoken paragraph break is the ASR marking the pause; the sentence ended.
        MakeRule(@"[ \t]*,[ \t]*\bneue[rn][ \t]+absatz\b[ \t,.;:!?]*", ".\n\n"),
        // The model already ended the sentence ("Anamnese. Neuer Absatz, die Klägerin"): the next word
        // starts the new paragraph or line, so it gets a capital (marker, CapitalizeAfterGluedColon).
        MakeRule(@"(?<=[.!?])[ \t]*\bneue[rn][ \t]+absatz\b[ \t,.;:!?]*", "\n\n\u0001"),
        MakeRule(@"(?<=[.!?])[ \t]*\bneue[ \t]+zeile\b[ \t,.;:!?]*", "\n\u0001"),
        // Paragraph break. A sentence end before it (". ! ? :") is kept, a stray comma is not.
        MakeRule(@"[ \t,;]*\bneue[rn][ \t]+absatz\b[ \t,.;:!?]*", "\n\n"),
        MakeRule(@"[ \t,;]*\bneue[ \t]+zeile\b[ \t,.;:!?]*", "\n"),
        // Parakeet glues this one onto the next word too ("neue Zeileinspektion"). "neue Zeilen..." is
        // left alone: that is the plural or a compound, said literally.
        MakeRule(@"[ \t,;]*\bneue[ \t]+zeile(?!n)(?=\p{L})", "\n\u0001"),
    };

    // Only in PunctuationMode.SpokenOnly, where the model adds no punctuation of its own. The mark
    // attaches to the previous word with one space after; after ". ? !" the next word is
    // capitalized (marker, see CapitalizeAfterGluedColon). Run after Rules.
    private static readonly Rule[] SpokenOnlyRules =
    {
        // "ich Schrägstrich sage" -> "ich/sage". Also "Schraegstrich", the mishearing "Schreckstrich",
        // and a split "Schräg Strich" / "Schräg-Strich". Spaces and stray ASR commas/semicolons before
        // it go; a period stays ("usw. Schrägstrich b" -> "usw./b"). "Schrägstriche" (plural) does not
        // match; the glued form ("Schrägstrichsage") does. Spoken-only: in automatic mode, and in
        // Dictate Prompt instructions, the word is an ordinary noun.
        MakeRule(@"[ \t,;]*\bschr(?:ä|ae|e)(?:g|ck)[ \t-]?strich\b[ \t,.;]*", "/"),
        MakeRule(@"[ \t,;]*\bschr(?:ä|ae|e)(?:g|ck)[ \t-]?strich(?!(?:e|en|es|s)\b)(?=\p{L})", "/"),
        // Parakeet glues the word onto the word before it: "Arbeitsmarktschrägstrich im ADL" ->
        // "Arbeitsmarkt/im ADL". Needs a letter directly before, so it never overlaps the rules above.
        MakeRule(@"(?<=\p{L})schr(?:ä|ae|e)(?:g|ck)[ \t-]?strich\b[ \t,.;]*", "/"),
        // Glued on both sides ("Arbeitsmarktschrägstrichbewegung" -> "Arbeitsmarkt/Bewegung"). Parakeet
        // wrote the right part lower-case only because it was one word with the rest; it starts a
        // word of its own now, so it gets a capital (marker). Case is not guessed anywhere else: a
        // glued right part with a space on the left keeps its letters.
        MakeRule(@"(?<=\p{L})schr(?:ä|ae|e)(?:g|ck)[ \t-]?strich(?!(?:e|en|es|s)\b)(?=\p{L})", "/\u0001"),
        // "3 Komma 5 cm" -> "3,5 cm": a decimal comma, no spaces. Must run before the plain "Komma" rule.
        // Only a single pair: "3 Komma 5 Komma 4" is a list and gets plain commas below. The same pair
        // said in words ("zwei Komma fünf" -> "2,5") is DecimalCommaBetweenNumberWords in Apply:
        // the words map to digits, so a replacement template cannot do it.
        MakeRule(@"(?<!\bkomma[ \t]\d{1,6})(?<=\d)[ \t]*\bkomma\b[ \t]*(?=\d)(?!\d+[ \t]*\bkomma\b[ \t]*\d)", ","),
        // A sentence mark the model wrote right before the word ("Gut. Fragezeichen") is swallowed.
        MakeRule(@"[ \t,;]*(?:[.!?][ \t]*)?\bfragezeichen\b[ \t,.;]*", "? \u0001"),
        MakeRule(@"[ \t,;]*(?:[.!?][ \t]*)?\bausrufezeichen\b[ \t,.;]*", "! \u0001"),
        // "Hüfte frei beweglich Semikolon Knie" -> "Hüfte frei beweglich; Knie". "Strichpunkt" is the
        // same mark. A comma the model put on either side is the pause, and goes. A period before the
        // word stays ("usw. Semikolon"), the same way "Komma" leaves one.
        MakeRule(@"[ \t,]*\b(?:semikolon|strichpunkt)\b[ \t,.;]*", "; "),
        // "drei Pünktchen", "drei Puenktchen", "Auslassungspunkte" -> "…", attached to the previous
        // word. Spaces and a stray comma or semicolon before it go; comma, period and semicolon after
        // it go too. Not "drei Punkte" (a score). Before "Punkt", so "Pünktchen" is not read as one.
        // Parakeet also writes the count as a digit and glues or hyphenates it ("GDB3-Pünktchen",
        // "3 Pünktchen", "drei-Pünktchen"); the digit may sit directly on the previous word.
        MakeRule(
            @"[ \t,;]*(?:(?:\bdrei|(?<!\d)3)[ \t-]*p(?:ü|ue)nktchen|\bauslassungspunkte)\b[ \t,.;]*",
            "… "),
        // A model period before or after the word ("vor. Punkt", "Punkt.") is swallowed: the
        // RepeatedMarks pass collapses the pair to one mark.
        // Not the noun: "Punkt 3", "Punkt drei", "Siehe Punkt 2.1", "zu Punkt", "der wichtigste Punkt".
        // A following number word counts as the number, same as a digit ("Unter Punkt drei steht").
        MakeRule(
            @"[ \t,;]*(?<!\b(?:zu|unter|im|in|der|den|dem|des|einen|einem|ein|jeden|letzten|wichtigste|wichtigsten)[ \t])\bpunkt\b(?![ \t]*(?:\d|(?:eins|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|elf|zwölf|zwoelf)\b))[ \t,.;]*",
            ". \u0001"),
        MakeRule(@"[ \t,;]*\bkomma\b[ \t,.;]*", ", "),
    };

    private static readonly Regex StripRegex = new(@"[,;]+(?=\s|\z)");
    private static readonly Regex SemicolonRegex = new(@";+(?=\s|\z)");
    // A model "?" / "!" at a word end, with the spaces and the word after it (group 1 and 2).
    private static readonly Regex GuessedSentenceMarkRegex = new(@"[?!]+(?=\s|\z)([ \t]+)?(\p{L}+)?");

    /// <summary>
    /// Removes the marks an offline model guessed on its own, for <see cref="PunctuationMode.SpokenOnly"/>:
    /// commas, semicolons, and, when no token timings are available, question marks and
    /// exclamation marks. Only marks followed by whitespace or the end of the text, so "1,2" and
    /// "a?b" survive. The model's "." and ":" stay: Parakeet often turns a spoken "Punkt" into "."
    /// itself, and stripping it destroyed marks the user did say. Without timings a "?" or "!" is
    /// different from a period: the model guesses it from intonation, while a spoken "Fragezeichen" /
    /// "Ausrufezeichen" is applied afterwards by <see cref="Apply"/>, so the user's own words still
    /// work and never double. When a stripped "?" or "!" ended a sentence the model capitalised the
    /// next word; that word is lower-cased again if it is a function word ("Und weiter" ->
    /// "und weiter"), nouns keep their capital (<see cref="LowercasingFunctionWord"/>).
    /// A model comma glued to a kept period ("z. B.,") loses the comma alone. Run before <see cref="Apply"/>.
    /// </summary>
    /// <param name="text">The model's text.</param>
    /// <param name="timedMarks">
    /// The text is Parakeet's, already filtered by token timing (ParakeetPunctuationTiming).
    /// The commas, periods, "?" and "!" left are the ones the timing called spoken, so only semicolons
    /// are removed here. Removing "?" and "!" turned a spoken "!" into the model's leftover ".".
    /// </param>
    public static string StripModelPunctuation(string text, bool timedMarks = false)
    {
        var result = (timedMarks ? SemicolonRegex : StripRegex).Replace(text, "");
        // Timing already kept or dropped each "?" and "!".
        if (timedMarks) return result;
        return GuessedSentenceMarkRegex.Replace(result, m =>
        {
            var gap = m.Groups[1];
            var word = m.Groups[2];
            if (gap.Success && word.Success)
                return gap.Value + (LowercasingFunctionWord(word.Value) ?? word.Value);
            return gap.Success ? gap.Value : "";
        });
    }

    /// <summary>
    /// The lower-case spelling of a German function word Parakeet capitalised on its own, or null.
    /// Whole word. "Sie", "Ihr" and "Ihnen" are not function words here: they are the formal
    /// address and must stay capital. Nouns and verbs are absent too. Live dictation uses this at a
    /// piece boundary, where the next piece starts a new decode and Parakeet capitalises its first word.
    /// </summary>
    public static string? LowercasingFunctionWord(string word)
    {
        if (word.Length == 0 || !char.IsUpper(word[0])) return null;
        if (!FunctionWords.Contains(word.ToLowerInvariant())) return null;
        return char.ToLowerInvariant(word[0]) + word.Substring(1);
    }

    /// <summary>Lower-case forms of function words and adverbs that are never nouns. "sie" is omitted on purpose.</summary>
    public static readonly IReadOnlySet<string> FunctionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "und", "oder", "aber", "sondern", "denn", "weil", "dass", "ob", "wenn", "als", "wie",
        "mit", "bei", "nach", "von", "vor", "zu", "zum", "zur", "aus", "auf", "an", "am",
        "im", "in", "über", "unter", "seit", "für", "durch", "gegen", "ohne", "um",
        "ist", "sind", "war", "waren", "hat", "haben", "wird", "wurde", "sich", "nicht",
        "auch", "noch", "schon", "sehr", "er", "es", "wir", "man",
        "der", "die", "das", "den", "dem", "des",
        "ein", "eine", "einen", "einem", "einer",
        "weiter", "dann", "jetzt", "hier", "da", "dort", "so", "nur", "mal", "wieder", "bitte",
        "doch", "eben", "gerade", "eigentlich", "vielleicht", "also", "dabei", "dazu", "damit",
        "daher", "deshalb", "trotzdem", "bereits", "immer", "nie", "oft", "etwas", "sonst", "zwar",
        "jedoch", "insgesamt", "ebenfalls", "weiterhin", "zudem", "außerdem", "allerdings", "eher",
        "kaum", "fast", "ganz",
    };

    private static readonly Rule TrailingSpaceBeforeBreak = MakeRule(@"[ \t]+(?=\n|$)", "");
    private static readonly Rule ExcessBreaks = MakeRule(@"\n{3,}", "\n\n");
    // "Punkt Punkt" leaves ". \u0001. \u0001". The copies collapse to one mark. The capital marker
    // counts as whitespace between them, so it is not what separates two marks, and the marker
    // after the last copy is left in place for CapitalizeAfterGluedColon ("Ende. Weiter").
    private static readonly Rule RepeatedMarks = MakeRule("([.,!?])(?:[ \\t\u0001]*\\1)+", "$1");

    private const string NumberWordBody = "null|eins|ein|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun";
    private static readonly Dictionary<string, string> NumberWordDigits = new()
    {
        ["null"] = "0", ["ein"] = "1", ["eins"] = "1", ["zwei"] = "2", ["drei"] = "3", ["vier"] = "4",
        ["fünf"] = "5", ["fuenf"] = "5", ["sechs"] = "6", ["sieben"] = "7", ["acht"] = "8", ["neun"] = "9",
    };
    private static readonly Regex DecimalNumberWordRegex = new(
        @"\b(" + NumberWordBody + @")\b[ \t]+\bkomma\b[ \t]+\b(" + NumberWordBody + @")\b", Opts);
    // A pair that continues a list ("zwei Komma fünf Komma vier", "Komma zwei Komma fünf") stays
    // words, the same way "3 Komma 5 Komma 4" stays digits with plain commas. `$` / `^` are the
    // ends of the text on either side of one pair, not of the whole line.
    private static readonly Regex DecimalChainBeforeRegex = new(@"\bkomma[ \t]+$", Opts);
    private static readonly Regex DecimalChainAfterRegex = new(
        @"^[ \t]+\bkomma\b[ \t]+\b(?:" + NumberWordBody + @")\b", Opts);

    /// <summary>
    /// "zwei Komma fünf" -> "2,5". Null when nothing matched, so <see cref="Apply"/> can return its input
    /// unchanged. Only one pair: a "Komma" directly before the first word, or a number word
    /// directly after the second, means a list.
    /// </summary>
    private static string? DecimalCommaBetweenNumberWords(string text)
    {
        var matches = DecimalNumberWordRegex.Matches(text);
        if (matches.Count == 0) return null;
        var result = text;
        var changed = false;
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var prefix = text.Substring(0, match.Index);
            var suffix = text.Substring(match.Index + match.Length);
            if (DecimalChainBeforeRegex.IsMatch(prefix) || DecimalChainAfterRegex.IsMatch(suffix)) continue;
            var left = match.Groups[1].Value.ToLowerInvariant();
            var right = match.Groups[2].Value.ToLowerInvariant();
            if (!NumberWordDigits.TryGetValue(left, out var d1) || !NumberWordDigits.TryGetValue(right, out var d2)) continue;
            result = result.Remove(match.Index, match.Length).Insert(match.Index, $"{d1},{d2}");
            changed = true;
        }
        return changed ? result : null;
    }

    /// <summary>
    /// Text with no command in it comes back unchanged (same string, whitespace included).
    /// <paramref name="keepsEdgeBreaks"/> trims only spaces and tabs at the start/end, not line breaks:
    /// for a piece in Dictate Live, where a trailing "neuer Absatz" must leave its "\n\n" for
    /// LiveChunkJoiner to lay out the next piece against; the default trims newlines too.
    /// </summary>
    public static string Apply(string text, PunctuationMode mode = PunctuationMode.Automatic, bool keepsEdgeBreaks = false)
    {
        var result = text;
        var changed = false;
        if (mode == PunctuationMode.SpokenOnly && DecimalCommaBetweenNumberWords(result) is { } decimals)
        {
            result = decimals;
            changed = true;
        }
        var rules = mode == PunctuationMode.SpokenOnly ? Rules.Concat(SpokenOnlyRules) : Rules;
        foreach (var rule in rules)
        {
            if (!rule.Regex.IsMatch(result)) continue;
            result = rule.Regex.Replace(result, rule.Template);
            changed = true;
        }
        // Before CapitalizeAfterGluedColon: the marker has to still be in the text, and a marker
        // between two copies of a mark has to count as the space that separates them.
        if (mode == PunctuationMode.SpokenOnly)
        {
            var collapsed = RepeatedMarks.Regex.Replace(result, RepeatedMarks.Template);
            if (collapsed != result)
            {
                result = collapsed;
                changed = true;
            }
        }
        if (!changed) return text;
        result = CapitalizeAfterGluedColon(result);
        result = TrailingSpaceBeforeBreak.Regex.Replace(result, TrailingSpaceBeforeBreak.Template);
        result = ExcessBreaks.Regex.Replace(result, ExcessBreaks.Template);
        return keepsEdgeBreaks ? result.Trim(' ', '\t') : result.Trim();
    }

    private static bool IsNewline(char ch) =>
        ch is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    /// <summary>
    /// The glued-command rules leave a U+0001 marker before the word they split off; this upper-cases
    /// that word's first letter ("Doppelpunktdruckschmerz" -> ": Druckschmerz") and drops the marker.
    /// </summary>
    private static string CapitalizeAfterGluedColon(string text)
    {
        if (!text.Contains('\u0001')) return text;
        var sb = new StringBuilder(text.Length);
        var upperNext = false;
        foreach (var ch in text)
        {
            if (ch == '\u0001') { upperNext = true; continue; }
            // The marker survives spaces and line breaks: "Punkt neue Zeile weiter" -> ".\nWeiter".
            if (upperNext && (ch == ' ' || ch == '\t' || IsNewline(ch)))
            {
                sb.Append(ch);
                continue;
            }
            sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }
        return sb.ToString();
    }
}
