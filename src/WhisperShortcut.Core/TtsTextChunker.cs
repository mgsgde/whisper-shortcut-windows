namespace WhisperShortcut.Core;

/// <summary>
/// Splits text into chunks at sentence boundaries for parallel TTS synthesis (port of the Mac TextChunker).
/// </summary>
/// <remarks>
/// The first chunk is capped separately and much lower (<c>firstChunkSize</c>): playback starts as soon as it is
/// synthesized, and TTS latency grows with the audio length, so a short opener decides how long the listener waits.
/// Caps after it grow geometrically by <c>growthFactor</c> until they reach <c>chunkSize</c>, so each chunk's audio
/// covers the next one's synthesis. Positions are C# char indexes (UTF-16), which is fine for prose.
/// </remarks>
public static class TtsTextChunker
{
    // Minimum chunk length as a share of the cap: lower for the first chunk, because a 40-char opening sentence is a
    // fine place to start talking while a mid-word hard split is not (Mac ttsFirstChunkMinSizeRatio / ttsChunkMinSizeRatio).
    private const double FirstChunkMinRatio = 0.3;
    private const double ChunkMinRatio = 0.7;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "dr", "mr", "mrs", "ms", "prof", "bzw", "ca", "etc", "usw", "vgl",
        "nr", "evtl", "inkl", "ggf", "e.g", "i.e", "vs", "st",
    };

    private static readonly HashSet<char> SentenceClosers = new()
    {
        '"', '”', '“', '»', '«', '\'', '’', ')', ']',
    };

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '…';

    private static bool IsSpacedDash(char c) => c is '–' or '—';

    /// <summary>
    /// Cap for chunk <paramref name="index"/>: <c>min(chunkSize, firstChunkSize * growthFactor^index)</c>.
    /// The first chunk size is clamped to <paramref name="chunkSize"/>.
    /// </summary>
    public static int ChunkCap(int index, int chunkSize, int firstChunkSize = ReadAloud.FirstChunkSizeChars,
        double growthFactor = 1.5)
    {
        var first = Math.Min(firstChunkSize, chunkSize);
        return Math.Min(chunkSize, (int)(first * Math.Pow(growthFactor, index)));
    }

    /// <summary>Trimmed text split as the Mac TextChunker does. Empty or whitespace input gives an empty list.</summary>
    public static IReadOnlyList<string> Split(string text, int chunkSize, int firstChunkSize = ReadAloud.FirstChunkSizeChars,
        double growthFactor = 1.5)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return Array.Empty<string>();

        var first = Math.Min(firstChunkSize, chunkSize);
        if (trimmed.Length <= first) return new[] { trimmed };

        var chunks = new List<string>();
        var current = 0;
        while (current < trimmed.Length)
        {
            // The index follows appended chunks only: a skipped whitespace-only span must not shift the ramp.
            var chunkIndex = chunks.Count;
            var remaining = trimmed.Substring(current);
            var cap = ChunkCap(chunkIndex, chunkSize, first, growthFactor);
            var minLength = (int)(cap * (chunkIndex == 0 ? FirstChunkMinRatio : ChunkMinRatio));

            if (remaining.Length <= cap)
            {
                chunks.Add(remaining);
                break;
            }

            var end = current + FindBestSplitPoint(remaining, cap, minLength, chunkSize);
            var piece = trimmed.Substring(current, end - current).Trim();
            if (piece.Length > 0) chunks.Add(piece);
            current = end;
        }
        return chunks;
    }

    // Where a reader would pause; the position is an index into the remaining text, after the punctuation and the
    // whitespace that follows it. The first matching rule wins:
    //  1. last sentence end in [minLength, cap]
    //  2. first sentence end in (cap, min(hardMax, cap * 1.5)]: finish the sentence
    //  3. last sentence end in [floor, minLength), floor = 25 % of cap
    //  4. last clause break in [floor, cap] (; or : plus whitespace, or a spaced dash)
    //  5. last comma plus whitespace in [floor, cap]
    //  6. last whitespace in [floor, cap]
    //  7. hard cut at cap
    private static int FindBestSplitPoint(string text, int cap, int minLength, int hardMax)
    {
        var floor = (int)(cap * 0.25);
        if (cap <= 0 || text.Length == 0) return 0;

        var overshootLimit = Math.Min(hardMax, (int)(cap * 1.5));
        var ends = SentenceSplitPositions(text, overshootLimit);

        var best = ends.LastOrDefault(p => p >= minLength && p <= cap);
        if (best > 0) return best;
        best = ends.FirstOrDefault(p => p > cap && p <= overshootLimit);
        if (best > 0) return best;
        best = ends.LastOrDefault(p => p >= floor && p < minLength);
        if (best > 0) return best;

        var clause = LastClauseSplit(text, floor, cap);
        if (clause is { } c) return c;
        var comma = LastCommaSplit(text, floor, cap);
        if (comma is { } k) return k;
        var space = LastWhitespaceSplit(text, floor, cap);
        if (space is { } w) return w;

        return Math.Max(Math.Min(cap, text.Length), 1);
    }

    // Positions after . ! ? ..., optional closing quotes or brackets, and the whitespace that follows.
    // A . after an abbreviation or an ordinal number is not an end.
    private static List<int> SentenceSplitPositions(string chars, int maxPosition)
    {
        var positions = new List<int>();
        var index = 0;
        while (index < chars.Length && index < maxPosition)
        {
            var ch = chars[index];
            var isAbbreviation = ch == '.' && IsAbbreviationDot(chars, index);
            if (IsSentenceEnd(ch) && !isAbbreviation)
            {
                var cursor = index + 1;
                while (cursor < chars.Length && SentenceClosers.Contains(chars[cursor])) cursor++;
                if (cursor >= chars.Length || char.IsWhiteSpace(chars[cursor]))
                {
                    while (cursor < chars.Length && char.IsWhiteSpace(chars[cursor])) cursor++;
                    if (cursor <= maxPosition)
                    {
                        positions.Add(cursor);
                        index = cursor;
                        continue;
                    }
                    break;
                }
            }
            index++;
        }
        return positions;
    }

    // A . does not end a sentence when the token before it is one letter (z. B., d. h.), a pure number
    // (am 3. Oktober), or a known abbreviation.
    private static bool IsAbbreviationDot(string chars, int dotIndex)
    {
        if (dotIndex <= 0) return false;

        var start = dotIndex;
        while (start > 0 && (char.IsLetter(chars[start - 1]) || char.IsNumber(chars[start - 1]))) start--;
        var immediate = chars.Substring(start, dotIndex - start);
        if (immediate.Length == 1 && char.IsLetter(immediate[0])) return true;
        if (immediate.Length > 0 && immediate.All(char.IsNumber)) return true;

        var extStart = dotIndex;
        while (extStart > 0 && (char.IsLetter(chars[extStart - 1]) || char.IsNumber(chars[extStart - 1]) || chars[extStart - 1] == '.'))
            extStart--;
        var extended = chars.Substring(extStart, dotIndex - extStart);
        return Abbreviations.Contains(immediate.ToLowerInvariant()) || Abbreviations.Contains(extended.ToLowerInvariant());
    }

    // Last ; or : followed by whitespace, or the index of a spaced en/em dash, inside [floor, cap].
    // A dash split leaves the dash at the start of the next chunk.
    private static int? LastClauseSplit(string chars, int floor, int cap)
    {
        int? best = null;
        var index = 0;
        while (index < chars.Length && index <= cap)
        {
            var ch = chars[index];
            if (ch is ';' or ':')
            {
                var cursor = index + 1;
                if (cursor < chars.Length && char.IsWhiteSpace(chars[cursor]))
                {
                    while (cursor < chars.Length && char.IsWhiteSpace(chars[cursor])) cursor++;
                    if (cursor >= floor && cursor <= cap) best = cursor;
                }
            }
            else if (IsSpacedDash(ch) && index > 0 && index + 1 < chars.Length
                     && char.IsWhiteSpace(chars[index - 1]) && char.IsWhiteSpace(chars[index + 1])
                     && index >= floor && index <= cap)
            {
                best = index;
            }
            index++;
        }
        return best;
    }

    // Last comma followed by whitespace whose split position falls in [floor, cap].
    private static int? LastCommaSplit(string chars, int floor, int cap)
    {
        int? best = null;
        for (var index = 0; index < chars.Length && index < cap; index++)
        {
            if (chars[index] != ',') continue;
            var cursor = index + 1;
            if (cursor < chars.Length && char.IsWhiteSpace(chars[cursor]))
            {
                while (cursor < chars.Length && char.IsWhiteSpace(chars[cursor])) cursor++;
                if (cursor >= floor && cursor <= cap) best = cursor;
            }
        }
        return best;
    }

    // Last whitespace run whose end falls in [floor, cap].
    private static int? LastWhitespaceSplit(string chars, int floor, int cap)
    {
        int? best = null;
        var index = 0;
        while (index < chars.Length && index < cap)
        {
            if (char.IsWhiteSpace(chars[index]))
            {
                var cursor = index + 1;
                while (cursor < chars.Length && char.IsWhiteSpace(chars[cursor])) cursor++;
                if (cursor >= floor && cursor <= cap) best = cursor;
                if (cursor > cap) break;
                index = cursor;
                continue;
            }
            index++;
        }
        return best;
    }
}
