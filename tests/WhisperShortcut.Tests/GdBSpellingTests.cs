using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of GdBSpellingTests.swift (Mac #493).</summary>
public class GdBSpellingTests
{
    [Theory]
    [InlineData("EinzelgdB30", "Einzel-GdB 30")]
    [InlineData("Einzel GDB 20", "Einzel-GdB 20")]
    [InlineData("GDB10", "GdB 10")]
    [InlineData("GesamtgdB", "Gesamt-GdB")]
    [InlineData("GesamtgdB50", "Gesamt-GdB 50")]
    [InlineData("eine Einzelgdb", "eine Einzel-GdB")]
    [InlineData("Einzel-GdB30", "Einzel-GdB 30")]
    [InlineData("GdB10", "GdB 10")]
    public void ParakeetSpellings(string input, string expected) =>
        Assert.Equal(expected, GdBSpelling.Normalize(input));

    [Theory]
    [InlineData(PunctuationMode.SpokenOnly)]
    [InlineData(PunctuationMode.Automatic)]
    public void FinishPieceDigits(PunctuationMode mode) =>
        Assert.Equal("Einzel-GdB 20", LiveDictation.FinishPiece("Einzel GdB zwanzig", mode));

    [Fact]
    public void Unchanged()
    {
        foreach (var text in new[] { "ein GdB von 50", "GdB", "AbGdB", "Hausgdbau", "einzeln reicht" })
        {
            Assert.Equal(text, GdBSpelling.Normalize(text));
            Assert.Equal(text, LiveDictation.FinishPiece(text, PunctuationMode.SpokenOnly));
        }
    }
}
