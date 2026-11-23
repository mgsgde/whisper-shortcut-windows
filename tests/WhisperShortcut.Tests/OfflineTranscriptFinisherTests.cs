using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

/// <summary>Port of the Mac's OfflineTranscriptFinisherTests (Mac #525).</summary>
public class OfflineTranscriptFinisherTests
{
    [Fact]
    public void KeepsEdgeBreaksFalseTrimsATrailingParagraphBreakTrueKeepsIt()
    {
        const string text = "Befund neuer Absatz";
        var trimmed = OfflineTranscriptFinisher.Finish(text, PunctuationMode.Automatic, timedMarks: false, keepsEdgeBreaks: false);
        var kept = OfflineTranscriptFinisher.Finish(text, PunctuationMode.Automatic, timedMarks: false, keepsEdgeBreaks: true);
        Assert.Equal("Befund", trimmed);
        Assert.Equal("Befund\n\n", kept);
        Assert.Equal(SpokenPunctuation.Apply(text), trimmed);
        Assert.Equal(SpokenPunctuation.Apply(text, keepsEdgeBreaks: true), kept);
    }

    [Fact]
    public void SpokenOnlyFinishDropsALeadingFillerAndSplitsAGluedPunkt()
    {
        // Capitalising is TextCleanup.ForDictation's job on this PC (it runs after the finisher), as for Live pieces.
        Assert.Equal("die Haut ist trocken.",
            OfflineTranscriptFinisher.Finish("äh die Haut ist trockenpunkt", PunctuationMode.SpokenOnly, timedMarks: false, keepsEdgeBreaks: false));
    }

    [Fact]
    public void PlainDictationNowWritesQuantityNumbersAsDigitsAndNormalisesGdB()
    {
        Assert.Equal("Blutdruck 140 zu 90",
            OfflineTranscriptFinisher.Finish("Blutdruck 140 zu neunzig", PunctuationMode.SpokenOnly, timedMarks: true, keepsEdgeBreaks: false));
        Assert.Equal("Einzel-GdB Psyche 20.",
            OfflineTranscriptFinisher.Finish("Einzel GdB Psyche zwanzig.", PunctuationMode.Automatic, timedMarks: true, keepsEdgeBreaks: false));
        // Prose stays prose (Mac #525 R248).
        Assert.Equal("Zimmer 12 drei Betten",
            OfflineTranscriptFinisher.Finish("Zimmer 12 drei Betten", PunctuationMode.Automatic, timedMarks: false, keepsEdgeBreaks: false));
    }

    [Fact]
    public void FinishPieceMatchesThePipelineDictateLiveUsedBeforeTheFinisher()
    {
        Assert.Equal("Blutdruck 140 zu 90",
            LiveDictation.FinishPiece("Blutdruck 140 zu neunzig", PunctuationMode.SpokenOnly, timedMarks: true));
        Assert.Equal("Einzel-GdB Psyche 20.",
            LiveDictation.FinishPiece("Einzel GdB Psyche zwanzig.", PunctuationMode.Automatic, timedMarks: true));
        Assert.Equal("Basistherapie.", LiveDictation.FinishPiece("Basistherapie ähm Punkt", PunctuationMode.SpokenOnly));
        Assert.Equal("was ist das", LiveDictation.FinishPiece("Hmm, was ist das", PunctuationMode.Automatic));
        Assert.Equal("Ramipril 2,5 mg",
            LiveDictation.FinishPiece("Ramipril zwei Komma fünf mg", PunctuationMode.SpokenOnly, timedMarks: true));
        Assert.Equal("Befund\n\n", LiveDictation.FinishPiece("Befund neuer Absatz", PunctuationMode.Automatic));
    }
}
