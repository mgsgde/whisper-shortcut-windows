using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// The one place offline transcript text is finished, for Dictate and Dictate Live. Port of the Mac's
/// OfflineTranscriptFinisher (Mac #525).
/// </summary>
public static class OfflineTranscriptFinisher
{
    /// <summary>
    /// Fillers dropped (a leading "Oh" before a spoken colon goes too), a glued trailing "…punkt" split back apart in
    /// "Only when spoken" mode, model punctuation stripped in that mode, then the spoken layout commands, GdB spelling,
    /// and quantity number words as digits.
    /// Spelling runs before the number words, in every punctuation mode, so "EinzelgdB" is already the token "GdB" when
    /// the value rule looks for it. Spoken commands have already run, so "drei Pünktchen" is already "…" and
    /// "zwei Komma fünf" already "2,5".
    /// <paramref name="keepsEdgeBreaks"/>: Dictate Live passes true so a trailing "neuer Absatz" stays a paragraph break
    /// for <see cref="LiveChunkJoiner"/>. Plain Dictate trims that edge break.
    /// <paramref name="timedMarks"/>: the text is Parakeet's, already filtered by token timing, so strip removes only
    /// semicolons and a "?" or "!" the timing kept stays. Without timings, model "?" and "!" drop.
    /// </summary>
    public static string Finish(string text, PunctuationMode mode, bool timedMarks, bool keepsEdgeBreaks)
    {
        var withoutFillers = RemovingFillers(text);
        var unglued = mode == PunctuationMode.SpokenOnly ? SplittingGluedTrailingPunkt(withoutFillers) : withoutFillers;
        var source = mode == PunctuationMode.SpokenOnly
            ? SpokenPunctuation.StripModelPunctuation(unglued, timedMarks)
            : unglued;
        var punctuated = SpokenPunctuation.Apply(source, mode, keepsEdgeBreaks: keepsEdgeBreaks, timedMarks: timedMarks);
        var spelled = GdBSpelling.Normalize(punctuated);
        return GermanNumberWords.DigitizingQuantities(spelled);
    }

    /// <summary>
    /// Parakeet glues a spoken "Punkt" before a pause onto the last word of the piece
    /// ("Die Haut ist trockenpunkt"). This splits it back to "trocken Punkt". Only the piece's last
    /// word is looked at. Real "-punkt" compounds stay via <see cref="SpokenPunctuation.IsRealPunktCompound"/>.
    /// The mid-piece glue is split by the spoken-punctuation rule, not here.
    /// </summary>
    public static string SplittingGluedTrailingPunkt(string text)
    {
        var match = GluedTrailingPunktRegex.Match(text);
        if (!match.Success) return text;
        var word = match.Groups[1].Value;
        var lower = word.ToLowerInvariant();
        if (lower is "punkt" or "doppelpunkt" or "strichpunkt") return text;
        if (SpokenPunctuation.IsRealPunktCompound(lower)) return text;
        var stem = match.Groups[2].Value;
        var at = match.Groups[1].Index;
        return string.Concat(text.AsSpan(0, at), stem, " Punkt", text.AsSpan(at + match.Groups[1].Length));
    }

    private static readonly Regex GluedTrailingPunktRegex = new(
        @"((\p{L}{2,})punkt)[.,;:!?…]*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Parakeet hears the breath before a lone spoken command as "Oh," ("Oh, Doppelpunkt." for a piece
    // that is only "Doppelpunkt"), which turned the colon into "Oh:". "Oh" stays a real word
    // everywhere else; it goes only at the start of a piece, right before a spoken colon.
    private static readonly Regex LeadingOhRegex = new(
        @"^[ \t]*oh[,.]?[ \t]+(?=doppel[ \t-]?(?:punkt|gun)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FillerSpaceRun = new(@"[ \t]{2,}");

    /// <summary>
    /// Parakeet writes fillers ("Mm-hmm", "Ähm") into the record. Whole word, case-insensitive, plus one comma, period or
    /// semicolon glued on, and the spaces around them. "Um", "ah", "aha", "achso" and "oh" stay: they are real German
    /// words. A text of only fillers comes back "". A leading "Oh" right before a spoken colon is the exception.
    /// </summary>
    private static string RemovingFillers(string text)
    {
        text = LeadingOhRegex.Replace(text, "");
        if (!LiveDictation.FillerRegex.IsMatch(text)) return text;
        var replaced = LiveDictation.FillerRegex.Replace(text, " ");
        return FillerSpaceRun.Replace(replaced, " ").Trim(' ', '\t');
    }
}
