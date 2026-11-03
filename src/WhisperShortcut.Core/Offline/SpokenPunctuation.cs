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
/// "Ausrufezeichen" and "Schrägstrich" are converted too.
/// </summary>
public static class SpokenPunctuation
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed record Rule(Regex Regex, string Template);

    private static Rule MakeRule(string pattern, string template) => new(new Regex(pattern, Opts), template);

    // Order matters: the colon runs first so "Befund Doppelpunkt neuer Absatz" keeps its colon when
    // the paragraph rule then strips the stray space before the break.
    private static readonly Rule[] Rules =
    {
        // "Anamnese Doppelpunkt Patient" / "Befund, Doppelpunkt. Inspektion" -> "Befund: Inspektion".
        MakeRule(@"[ \t,.;]*\bdoppelpunkt\b[ \t,.;:]*", ": "),
        // Parakeet sometimes glues the command onto the next word ("Doppelpunktdruckschmerz"); the
        // marker makes the next letter upper case afterwards. Inflections of the noun stay alone.
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
    };

    // Only in PunctuationMode.SpokenOnly, where the model adds no punctuation of its own. The mark
    // attaches to the previous word with one space after; after ". ? !" the next word is
    // capitalized (marker). Run after Rules.
    private static readonly Rule[] SpokenOnlyRules =
    {
        // "ich Schrägstrich sage" -> "ich/sage". A period stays ("usw. Schrägstrich b" -> "usw./b").
        // "Schrägstriche" (plural) does not match; the glued form ("Schrägstrichsage") does.
        MakeRule(@"[ \t,;]*\bschr(?:ä|ae)gstrich\b[ \t,.;]*", "/"),
        MakeRule(@"[ \t,;]*\bschr(?:ä|ae)gstrich(?!(?:e|en|es|s)\b)(?=\p{L})", "/"),
        // "3 Komma 5 cm" -> "3,5 cm": a decimal comma, no spaces. Must run before the plain "Komma" rule.
        // Only a single pair: "3 Komma 5 Komma 4" is a list and gets plain commas below.
        MakeRule(@"(?<!\bkomma[ \t]\d{1,6})(?<=\d)[ \t]*\bkomma\b[ \t]*(?=\d)(?!\d+[ \t]*\bkomma\b[ \t]*\d)", ","),
        MakeRule(@"[ \t,;]*\bfragezeichen\b[ \t,.;]*", "? \u0001"),
        MakeRule(@"[ \t,;]*\bausrufezeichen\b[ \t,.;]*", "! \u0001"),
        // Not the noun: "Punkt 3", "Siehe Punkt 2.1", "zu Punkt", "der wichtigste Punkt".
        MakeRule(
            @"[ \t,;]*(?<!\b(?:zu|unter|im|in|der|den|dem|des|einen|einem|ein|jeden|letzten|wichtigste|wichtigsten)[ \t])\bpunkt\b(?![ \t]*\d)[ \t,.;]*",
            ". \u0001"),
        MakeRule(@"[ \t,;]*\bkomma\b[ \t,.;]*", ", "),
    };

    private static readonly Regex StripRegex = new(@"[.,;!?]+(?=\s|\z)");

    /// <summary>Abbreviations whose period is kept (case-sensitive, whole word).</summary>
    private static readonly HashSet<string> KeptAbbreviations = new(StringComparer.Ordinal)
    {
        "Dr", "Prof", "Nr", "z", "B", "u", "a", "bzw", "ca", "usw", "ggf", "S", "Abs", "Art", "vgl",
        "evtl", "inkl", "Hr", "Fr", "St", "Tel", "Str",
        "med", "habil", "Az", "Abb", "Anl", "Bl", "geb", "tgl", "f", "ff", "Ziff", "bzgl", "etc", "sog",
        "lt", "gem", "Mio", "Std", "Min", "max", "min", "Jh", "Hrsg", "Aufl", "Kap", "Tab", "Anm",
        "Mrd", "Tsd", "Dipl", "Ing", "dent", "rer", "nat",
    };

    /// <summary>
    /// Removes the punctuation an offline model inserted on its own (. , ; ! ?) for
    /// <see cref="PunctuationMode.SpokenOnly"/>. Only marks followed by whitespace or the end of the text, so
    /// "3.5" and "1,2" survive. A single period stays when it belongs to the text rather than ending a
    /// sentence: after a digit and before a space ("3. November") and after one of the kept abbreviations
    /// ("Dr. Müller", "z. B.", any single letter). A model comma glued to a kept period is dropped alone.
    /// Run before <see cref="Apply"/>.
    /// </summary>
    public static string StripModelPunctuation(string text)
    {
        var result = text;
        var matches = StripRegex.Matches(text);
        for (var m = matches.Count - 1; m >= 0; m--)
        {
            var start = matches[m].Index;
            var length = matches[m].Length;
            if (text[start] == '.' && PeriodBelongsToText(text, start))
            {
                // Keep the text's period; a model mark glued to it (".,") still goes.
                if (length == 1) continue;
                start += 1;
                length -= 1;
            }
            result = result.Remove(start, length);
        }
        return result;
    }

    private static bool PeriodBelongsToText(string text, int periodIndex)
    {
        // The word (letters) right before the period.
        var start = periodIndex;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        if (start < periodIndex)
        {
            var word = text.Substring(start, periodIndex - start);
            // Any single letter is an abbreviation ("d. h.", "i. d. R."); "Hauptstr." ends in "str".
            return word.Length == 1 || KeptAbbreviations.Contains(word)
                || word.ToLowerInvariant().EndsWith("str", StringComparison.Ordinal);
        }
        // Directly after a digit, and followed by a space (not the end): an ordinal or a date.
        if (periodIndex == 0 || !char.IsDigit(text[periodIndex - 1])) return false;
        var next = periodIndex + 1;
        return next < text.Length && text[next] == ' ';
    }

    private static readonly Rule TrailingSpaceBeforeBreak = MakeRule(@"[ \t]+(?=\n|$)", "");
    private static readonly Rule ExcessBreaks = MakeRule(@"\n{3,}", "\n\n");

    /// <summary>Text with no command in it comes back unchanged (same string, whitespace included).</summary>
    public static string Apply(string text, PunctuationMode mode = PunctuationMode.Automatic)
    {
        var result = text;
        var changed = false;
        var rules = mode == PunctuationMode.SpokenOnly ? Rules.Concat(SpokenOnlyRules) : Rules;
        foreach (var rule in rules)
        {
            if (!rule.Regex.IsMatch(result)) continue;
            result = rule.Regex.Replace(result, rule.Template);
            changed = true;
        }
        if (!changed) return text;
        result = CapitalizeAfterGluedColon(result);
        result = TrailingSpaceBeforeBreak.Regex.Replace(result, TrailingSpaceBeforeBreak.Template);
        result = ExcessBreaks.Regex.Replace(result, ExcessBreaks.Template);
        return result.Trim();
    }

    private static string CapitalizeAfterGluedColon(string text)
    {
        if (!text.Contains('\u0001')) return text;
        var sb = new StringBuilder(text.Length);
        var upperNext = false;
        foreach (var ch in text)
        {
            if (ch == '\u0001') { upperNext = true; continue; }
            // The marker survives spaces and line breaks: "Punkt neue Zeile weiter" -> ".\nWeiter".
            if (upperNext && (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r'))
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
