using System.Text.RegularExpressions;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class TtsTextChunkerTests
{
    private const int Gemini = ReadAloud.GeminiChunkSizeChars;

    private static string Sentences(int count) =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"Das ist der {i}. Satz dieses Textes und er ist ein wenig länger."));

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void EmptyOrWhitespace_GivesNoChunks(string text) =>
        Assert.Empty(TtsTextChunker.Split(text, Gemini));

    [Fact]
    public void ShortText_IsOneChunk_Trimmed()
    {
        var chunks = TtsTextChunker.Split("  Kurzer Text.\n", Gemini);
        Assert.Equal(new[] { "Kurzer Text." }, chunks);
    }

    [Fact]
    public void ChunkCap_RampsForGemini_120_180_260_260()
    {
        Assert.Equal(120, TtsTextChunker.ChunkCap(0, Gemini));
        Assert.Equal(180, TtsTextChunker.ChunkCap(1, Gemini));
        Assert.Equal(260, TtsTextChunker.ChunkCap(2, Gemini));
        Assert.Equal(260, TtsTextChunker.ChunkCap(3, Gemini));
        Assert.Equal(120, TtsTextChunker.ChunkCap(0, 500));
        Assert.Equal(270, TtsTextChunker.ChunkCap(2, 500));
        Assert.Equal(500, TtsTextChunker.ChunkCap(9, 500));
        // The first chunk size never exceeds the chunk size.
        Assert.Equal(100, TtsTextChunker.ChunkCap(0, 100, 120));
    }

    [Theory]
    [InlineData(Gemini)]
    [InlineData(ReadAloud.ChunkSizeChars)]
    public void NoChunkAboveTheCap_NoneEmpty_AndJoinedEqualsInput(int chunkSize)
    {
        var text = Sentences(60);
        var chunks = TtsTextChunker.Split(text, chunkSize);
        Assert.True(chunks.Count > 3);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.NotEqual("", chunks[i]);
            Assert.Equal(chunks[i], chunks[i].Trim());
            Assert.True(chunks[i].Length <= chunkSize, $"chunk {i} has {chunks[i].Length} chars");
            Assert.True(chunks[i].Length <= TtsTextChunker.ChunkCap(i, chunkSize) * 1.5 + 1, $"chunk {i} overshoots too far");
        }
        Assert.Equal(Normalize(text), Normalize(string.Join(" ", chunks)));
    }

    [Fact]
    public void FirstChunk_IsShort_AndEndsOnASentence()
    {
        var chunks = TtsTextChunker.Split(Sentences(10), Gemini);
        Assert.True(chunks[0].Length <= 180);          // 120 cap, at most 1.5x to finish the sentence
        Assert.EndsWith("länger.", chunks[0]);
    }

    [Fact]
    public void AbbreviationDots_DoNotEndAChunk()
    {
        // Sentence padding so that "z. B." and "am 3. Oktober" sit right where a chunk would be cut.
        for (var pad = 60; pad < 140; pad++)
        {
            var text = new string('a', pad) + " bunte Sachen, z. B. Äpfel und Birnen. Wir treffen uns am 3. Oktober im Park, um zu reden. "
                + Sentences(6);
            var chunks = TtsTextChunker.Split(text, Gemini);
            foreach (var c in chunks)
            {
                Assert.False(c.EndsWith("z. B.", StringComparison.Ordinal), $"pad {pad}: chunk ends with 'z. B.': {c}");
                Assert.False(c.EndsWith("z.", StringComparison.Ordinal), $"pad {pad}: chunk ends with 'z.'");
                Assert.False(c.EndsWith("am 3.", StringComparison.Ordinal), $"pad {pad}: chunk ends with 'am 3.'");
                Assert.False(Regex.IsMatch(c, @"\b\d+\.$"), $"pad {pad}: chunk ends with an ordinal: {c}");
            }
            Assert.Equal(Normalize(text), Normalize(string.Join(" ", chunks)));
        }
    }

    [Fact]
    public void KnownAbbreviations_DoNotEndAChunk()
    {
        var text = "Heute sprach Dr. Müller mit Prof. Schmidt über Äpfel, Birnen usw. und vieles mehr aus dem Garten, "
            + "und die beiden verstanden sich bestens. " + Sentences(5);
        var chunks = TtsTextChunker.Split(text, Gemini);
        foreach (var c in chunks)
        {
            Assert.False(c.EndsWith("Dr.", StringComparison.Ordinal));
            Assert.False(c.EndsWith("Prof.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void LongTextWithoutPunctuation_SplitsOnWhitespace()
    {
        var text = string.Join(" ", Enumerable.Repeat("wort", 200));
        var chunks = TtsTextChunker.Split(text, Gemini);
        Assert.True(chunks.Count > 3);
        foreach (var c in chunks)
        {
            Assert.True(c.Length <= Gemini);
            // Never a word cut in half: every token is a full "wort".
            Assert.All(c.Split(' '), t => Assert.Equal("wort", t));
        }
        Assert.Equal(text, string.Join(" ", chunks));
    }

    [Fact]
    public void TextWithoutAnyBreak_IsCutHard_AtTheCap()
    {
        var text = new string('x', 700);
        var chunks = TtsTextChunker.Split(text, Gemini);
        Assert.Equal(120, chunks[0].Length);
        Assert.Equal(180, chunks[1].Length);
        Assert.Equal(260, chunks[2].Length);
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void ClauseBreak_IsPreferredOverAHardCut()
    {
        // No sentence end, but a colon and a spaced dash.
        var text = string.Join(" ", Enumerable.Repeat("alpha beta gamma", 5)) + ": " + string.Join(" ", Enumerable.Repeat("delta epsilon", 40));
        var chunks = TtsTextChunker.Split(text, Gemini);
        Assert.EndsWith(":", chunks[0]);
    }

    [Fact]
    public void Wide_Text_Keeps_ParagraphBreaks_OutOfChunkEdges()
    {
        var text = Sentences(4) + "\n\n" + Sentences(4);
        var chunks = TtsTextChunker.Split(text, ReadAloud.ChunkSizeChars);
        Assert.All(chunks, c => Assert.Equal(c.Trim(), c));
        Assert.Equal(Normalize(text), Normalize(string.Join(" ", chunks)));
    }
}
