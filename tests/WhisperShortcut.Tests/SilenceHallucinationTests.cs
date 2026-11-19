using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Known silence-hallucination phrases are dropped only for clips under 2 s.
/// Port of the phrase half of ValidateSpeechTextTests (Mac #367). The Whisper VAD gate and the
/// settings toggle are not ported. An empty result is "no speech", which on Windows is "".
/// </summary>
public class SilenceHallucinationTests
{
    [Theory]
    [InlineData("Thank you.")]
    [InlineData("thank you")]
    [InlineData("Thank you!")]
    [InlineData("Thanks for watching!")]
    [InlineData("thanks for watching")]
    [InlineData("You")]
    [InlineData("Bye.")]
    [InlineData("bye")]
    [InlineData("Vielen Dank.")]
    [InlineData("Untertitel der amara.org-Community")]
    [InlineData("Subtitles by the Amara.org Community")]
    [InlineData("Please subscribe")]
    [InlineData("Bitte abonnieren.")]
    [InlineData("  Thank you.  ")]
    public void ATranscriptThatIsOnlyAKnownHallucinationIsNoSpeech(string text) =>
        Assert.Equal("", TextCleanup.ForDictation(text, audioSeconds: 1.0));

    [Theory]
    [InlineData("Thank you for the update on the lab results.")]
    [InlineData("I said thank you and left.")]
    [InlineData("Bye, see you tomorrow at nine.")]
    [InlineData("Please subscribe to the practice newsletter before Friday.")]
    public void ARealSentenceContainingAPhantomPhraseIsKept(string text) =>
        Assert.Equal(text, TextCleanup.ForDictation(text, audioSeconds: 1.0));

    [Theory]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void ALongClipKeepsASinglePhraseTranscript(double seconds) =>
        Assert.Equal("Vielen Dank.", TextCleanup.ForDictation("Vielen Dank.", audioSeconds: seconds));

    [Fact]
    public void AnUnknownLengthClipKeepsASinglePhraseTranscript() =>
        Assert.Equal("Vielen Dank.", TextCleanup.ForDictation("Vielen Dank."));

    [Fact]
    public void EmptyIsStillNoSpeech() =>
        Assert.Equal("", TextCleanup.ForDictation("   ", audioSeconds: 1.0));

    [Fact]
    public void TheComparisonFormStripsCaseWhitespaceAndTrailingPunctuation()
    {
        Assert.Equal("thank you", TextCleanup.PhantomComparisonForm("  Thank YOU.  "));
        Assert.Equal("bye", TextCleanup.PhantomComparisonForm("Bye!"));
        Assert.Equal("thank you, dr. meyer", TextCleanup.PhantomComparisonForm("Thank you, Dr. Meyer."));
    }
}
