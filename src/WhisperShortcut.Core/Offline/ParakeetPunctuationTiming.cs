using System.Text;
using System.Text.RegularExpressions;

namespace WhisperShortcut.Core;

/// <summary>
/// Tells a punctuation mark the speaker SAID from one Parakeet GUESSED, for
/// <see cref="PunctuationMode.SpokenOnly"/>. Port of the Mac app's ParakeetPunctuationTiming.swift.
///
/// Parakeet writes a spoken "Komma" / "Punkt" / "Fragezeichen" / "Ausrufezeichen" as "," / "." /
/// "?" / "!" itself, so the text alone cannot tell those apart from the marks it adds from
/// intonation. The token timings can: a spoken mark has speech time behind it (the speaker said a
/// word and kept going or let the recording run on), a guessed mark sits flush against the next
/// word or against the end of the audio. Measured on the Mac over 6 voices and 228 marks:
///
/// - Mid-text ",": gap to the next token >= 0.120 s keeps 34 of 43 spoken and 0 of 34 guessed.
/// - Final ".": audio tail after the mark >= 0.400 s (21/23 spoken vs 0/42 guessed piece-end marks).
///
/// "?" and "!" use the same timing rule as "," and ".". When no timings are available,
/// <see cref="SpokenPunctuation.StripModelPunctuation"/> still drops the model's "?" and "!".
/// </summary>
public static class ParakeetPunctuationTiming
{
    /// <summary>Mid-text mark ("," "." "?" "!"): minimum silence to the next token for the mark to count as spoken.</summary>
    public const double MinGapToNextToken = 0.120;

    /// <summary>
    /// Final mark ("," "." "?" "!"): minimum audio left after it. Stricter than the mid-text gap
    /// because a recording always runs a little past the last word.
    /// </summary>
    public const double MinTailAfterFinalMark = 0.400;

    /// <summary>One Parakeet token with its timing in seconds.</summary>
    public sealed record Token(string Text, double Start, double End);

    /// <summary>The result of applying the rule to a text.</summary>
    public sealed record Filtered(string Text, int Kept, int Dropped);

    private const char WordMarker = '▁';
    private const string MarkCharacters = ",.?!";

    /// <summary>The token without SentencePiece's word marker and surrounding whitespace.</summary>
    private static string Bare(Token token) => token.Text.Trim(' ', '\t', WordMarker);

    /// <summary>sherpa-onnx tokens may carry the marker or a plain leading space; both start a word.</summary>
    private static bool StartsWord(Token token) =>
        token.Text.Length > 0 && (token.Text[0] == WordMarker || char.IsWhiteSpace(token.Text[0]));

    /// <summary>
    /// Indexes of the tokens that are a mark at the end of a word. A "." or "," inside a number or
    /// an abbreviation token run ("3.5", "1,2") is part of the next word's pieces, not a mark.
    /// </summary>
    private static List<int> MarkIndexes(IReadOnlyList<Token> tokens)
    {
        var result = new List<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var mark = Bare(tokens[index]);
            if (mark.Length != 1 || MarkCharacters.IndexOf(mark[0]) < 0) continue;
            if (index == tokens.Count - 1 || StartsWord(tokens[index + 1])) { result.Add(index); continue; }
            // Parakeet sometimes writes a spoken "Komma" glued to the next word ("50,Merkzeichen"):
            // the next token then continues without a word marker and starts with a letter.
            var next = Bare(tokens[index + 1]);
            if (mark[0] == ',' && next.Length > 0 && char.IsLetter(next[0])) result.Add(index);
        }
        return result;
    }

    private static readonly Regex GluedCommaRegex = new(@",(?=\p{L})");

    /// <summary>
    /// Puts the missing space after a comma glued to a letter (",Merkzeichen" -> ", Merkzeichen").
    /// A comma before a digit ("3,5") is a decimal comma and stays. Applied to every Parakeet
    /// output, in every punctuation mode.
    /// </summary>
    public static string SpacingGluedCommas(string text) => GluedCommaRegex.Replace(text, ", ");

    /// <summary>
    /// One verdict per mark token, in order: true = the speaker said it, false = Parakeet guessed it.
    /// "," "." "?" and "!" share one rule: a mark with a following token needs
    /// <see cref="MinGapToNextToken"/> of silence before that token; the last mark needs
    /// <see cref="MinTailAfterFinalMark"/> of audio after it.
    /// </summary>
    public static List<bool> SpokenMarks(IReadOnlyList<Token> tokens, double audioDuration) =>
        MarkIndexes(tokens).Select(index => index == tokens.Count - 1
            ? audioDuration - tokens[index].End >= MinTailAfterFinalMark
            : tokens[index + 1].Start - tokens[index].End >= MinGapToNextToken).ToList();

    /// <summary>
    /// Removes the guessed marks from <paramref name="text"/>. The n-th mark in the text (a mark character
    /// at the end of a word) is the n-th mark token. Null when the counts differ, so the caller falls back.
    ///
    /// A dropped "." that was followed by a capitalised function word lowers that word again
    /// ("Hallo. Und weiter" -> "Hallo und weiter"); nouns keep their capital. A mid-text "." after a
    /// number or a short abbreviation ("5. Lendenwirbel", "z. B.") is never dropped: Parakeet
    /// writes it without a pause and it is not a sentence end.
    /// </summary>
    public static Filtered? Filter(string text, IReadOnlyList<Token> tokens, double audioDuration)
    {
        var verdicts = SpokenMarks(tokens, audioDuration);
        var characters = text.ToCharArray();
        var markPositions = new List<int>();
        var gluedCommas = new HashSet<int>();
        for (var i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (MarkCharacters.IndexOf(character) < 0) continue;
            var endsWord = i + 1 == characters.Length || char.IsWhiteSpace(characters[i + 1]);
            var followsWord = i > 0 && !char.IsWhiteSpace(characters[i - 1]);
            var glued = character == ',' && i + 1 < characters.Length && char.IsLetter(characters[i + 1]);
            if ((endsWord || glued) && followsWord)
            {
                markPositions.Add(i);
                if (glued) gluedCommas.Add(i);
            }
        }
        if (markPositions.Count != verdicts.Count) return null;

        var drop = new HashSet<int>();
        var kept = 0;
        for (var n = 0; n < markPositions.Count; n++)
        {
            var position = markPositions[n];
            var spoken = verdicts[n];
            var isFinal = true;
            for (var k = position + 1; k < characters.Length; k++)
                if (!char.IsWhiteSpace(characters[k])) { isFinal = false; break; }
            if (!spoken && characters[position] == '.' && !isFinal && IsAbbreviationOrOrdinal(position, characters))
                spoken = true;
            if (spoken) kept++; else drop.Add(position);
        }

        var result = new StringBuilder(text.Length);
        var lowerNextWord = false;
        var index = 0;
        while (index < characters.Length)
        {
            var character = characters[index];
            if (drop.Contains(index))
            {
                if (gluedCommas.Contains(index)) result.Append(' ');
                if (character is '.' or '?' or '!') lowerNextWord = true;
                index++;
                continue;
            }
            if (lowerNextWord && char.IsLetter(character))
            {
                var end = index;
                while (end < characters.Length && char.IsLetter(characters[end])) end++;
                var word = new string(characters, index, end - index);
                result.Append(SpokenPunctuation.LowercasingFunctionWord(word) ?? word);
                index = end;
                lowerNextWord = false;
                continue;
            }
            if (!char.IsWhiteSpace(character)) lowerNextWord = false;
            result.Append(character);
            if (gluedCommas.Contains(index)) result.Append(' ');
            index++;
        }
        // A dropped final mark leaves trailing spaces only if the text had them; keep the text as is.
        return new Filtered(result.ToString(), kept, drop.Count);
    }

    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "usw", "bzw", "ca", "vgl", "dr", "nr", "evtl", "ggf", "inkl", "etc", "bspw", "ggfs", "max",
        "min", "mind", "ev", "sog", "od", "bez", "betr", "zzgl", "abs", "str",
    };

    private static bool IsAbbreviationOrOrdinal(int position, char[] characters)
    {
        var start = position;
        while (start > 0 && (char.IsLetter(characters[start - 1]) || char.IsNumber(characters[start - 1]))) start--;
        var word = new string(characters, start, position - start);
        if (word.Length == 0) return false;
        if (word.All(char.IsNumber)) return true;
        if (word.Length == 1) return true;
        return Abbreviations.Contains(word.ToLowerInvariant());
    }

    /// <summary>
    /// <paramref name="text"/> with the marks Parakeet guessed removed, using the token timings. When they
    /// are missing or do not line up with the text, the glued commas are spaced and
    /// <paramref name="timed"/> is false: the caller then runs
    /// <see cref="SpokenPunctuation.StripModelPunctuation"/> without timedMarks, as the Mac does.
    /// Counts only, never the dictated text, go to the log.
    /// </summary>
    public static string KeepSpokenMarks(string text, IReadOnlyList<Token>? tokens, double audioSeconds, out bool timed)
    {
        if (tokens != null && Filter(text, tokens, audioSeconds) is { } filtered)
        {
            Log.Info($"LOCAL-SPEECH: punctuation timing kept={filtered.Kept} dropped={filtered.Dropped}");
            timed = true;
            return filtered.Text;
        }
        Log.Info("LOCAL-SPEECH: punctuation timing skipped");
        timed = false;
        return SpacingGluedCommas(text);
    }
}
