using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Fillers ("äh", "ähm", "hmm", …) are stripped on every dictation transcript, not only Live.
/// A filler at the start or after a sentence end also capitalises the next letter.
/// Port of TranscriptFillerRemovalTests (Mac #507). The word list is LiveDictation.FillerRegex.
/// </summary>
public class TranscriptFillerRemovalTests
{
    [Theory]
    [InlineData("Das ist ähm gut.", "Das ist gut.", "ähm")]
    [InlineData("Das ist Äh, gut.", "Das ist gut.", "Äh,")]
    [InlineData("Das ist öhm gut.", "Das ist gut.", "öhm")]
    [InlineData("Das ist Mm-hmm gut.", "Das ist gut.", "Mm-hmm")]
    [InlineData("Das ist mhm gut.", "Das ist gut.", "mhm")]
    [InlineData("Das ist hmm gut.", "Das ist gut.", "hmm")]
    public void FillersDropMidSentenceWithoutTouchingTheNextWordsCase(string input, string expected, string label) =>
        Assert.True(TextCleanup.RemovingFillers(input) == expected, label);

    [Theory]
    [InlineData("ähm das ist gut.", "Das ist gut.", "ähm at start")]
    [InlineData("Äh, das ist gut.", "Das ist gut.", "Äh, at start")]
    [InlineData("öhm das ist gut.", "Das ist gut.", "öhm at start")]
    [InlineData("Mm-hmm das ist gut.", "Das ist gut.", "Mm-hmm at start")]
    [InlineData("mhm das ist gut.", "Das ist gut.", "mhm at start")]
    [InlineData("hmm das ist gut.", "Das ist gut.", "hmm at start")]
    [InlineData("Ähm, ich komme", "Ich komme", "Ähm, ich komme")]
    [InlineData("Okay. Ähm, das passt", "Okay. Das passt", "after period")]
    [InlineData("Okay! ähm das passt", "Okay! Das passt", "after exclamation")]
    [InlineData("Okay? ähm das passt", "Okay? Das passt", "after question")]
    [InlineData("Okay.\nähm das passt", "Okay.\n Das passt", "after newline")]
    public void AFillerAtTheStartOrAfterASentenceEndCapitalisesTheNextLetter(string input, string expected, string label) =>
        Assert.True(TextCleanup.RemovingFillers(input) == expected, label);

    [Theory]
    [InlineData("Das ist ähnlich.", "Das ist ähnlich.", "ähnlich")]
    [InlineData("Um zehn Uhr", "Um zehn Uhr", "Um")]
    [InlineData("oh nein", "oh nein", "oh")]
    [InlineData("achso, dann", "achso, dann", "achso")]
    [InlineData("Herr Ehmann kommt", "Herr Ehmann kommt", "Ehmann")]
    public void RealWordsThatOnlyLookLikeFillersStay(string input, string expected, string label) =>
        Assert.True(TextCleanup.RemovingFillers(input) == expected, label);

    [Theory]
    [InlineData("ähm")]
    [InlineData("Äh,")]
    [InlineData("öhm")]
    [InlineData("Mm-hmm")]
    [InlineData("mhm")]
    [InlineData("hmm")]
    [InlineData("hmm ähm")]
    public void AFillerOnlyClipComesBackEmpty(string input) =>
        Assert.Equal("", TextCleanup.RemovingFillers(input));

    [Fact]
    public void ALineBreakSurvivesFillerRemoval() =>
        Assert.Equal("erste Zeile\nzweite Zeile", TextCleanup.RemovingFillers("erste Zeile\nzweite ähm Zeile"));

    [Fact]
    public void ForDictationStripsALeadingFillerAndTreatsFillerOnlyAsNoSpeech()
    {
        // The filler step yields "Ich komme"; ForDictation still adds the trailing period it adds to any dictation.
        Assert.Equal("Ich komme", TextCleanup.RemovingFillers("Ähm, ich komme"));
        Assert.Equal("Ich komme.", TextCleanup.ForDictation("Ähm, ich komme"));
        Assert.Equal("Das ist gut.", TextCleanup.ForDictation("Ähm, das ist gut."));
        Assert.Equal("", TextCleanup.ForDictation("ähm"));
    }
}
