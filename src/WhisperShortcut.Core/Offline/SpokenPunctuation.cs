using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Turns spoken layout commands into layout in offline transcripts. Exact port of the Mac app's
/// SpokenPunctuation.swift: Parakeet writes a spoken "Doppelpunkt" or "neuer Absatz" out as words,
/// where cloud models already produce the punctuation.
/// Deliberately narrow: "Punkt", "Komma" and "Fragezeichen" are ordinary German words and stay.
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
        // Paragraph break. A sentence end before it (". ! ? :") is kept, a stray comma is not.
        MakeRule(@"[ \t,;]*\bneue[rn][ \t]+absatz\b[ \t,.;:!?]*", "\n\n"),
        MakeRule(@"[ \t,;]*\bneue[ \t]+zeile\b[ \t,.;:!?]*", "\n"),
    };

    private static readonly Rule TrailingSpaceBeforeBreak = MakeRule(@"[ \t]+(?=\n|$)", "");
    private static readonly Rule ExcessBreaks = MakeRule(@"\n{3,}", "\n\n");

    /// <summary>Text with no command in it comes back unchanged (same string, whitespace included).</summary>
    public static string Apply(string text)
    {
        var result = text;
        var changed = false;
        foreach (var rule in Rules)
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
            sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
            upperNext = false;
        }
        return sb.ToString();
    }
}
