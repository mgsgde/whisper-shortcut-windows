using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class MicMatchTests
{
    [Fact]
    public void ExactMatchWinsOverInstancePrefix()
    {
        const string stored = "Microphone (TONOR TC30 Audio De";
        Assert.Equal(1, MicMatch.Find(stored, new[]
        {
            "Microphone (2- TONOR TC30 Audio",
            stored,
        }));
    }

    [Fact]
    public void TruncatedNameMatchesReenumeratedDevice()
    {
        Assert.Equal(1, MicMatch.Find("Microphone (TONOR TC30 Audio De", new[]
        {
            "Microphone (Realtek(R) Audio)",
            "Microphone (2- TONOR TC30 Audio",
        }));
    }

    [Fact]
    public void ReenumeratedNameMatchesStoredTruncation()
    {
        Assert.Equal(0, MicMatch.Find(
            "Microphone (2- TONOR TC30 Audio",
            new[] { "Microphone (TONOR TC30 Audio De" }));
    }

    [Fact]
    public void ShortNamesMatchOnceInstancePrefixIsRemoved()
    {
        Assert.Equal(0, MicMatch.Find(
            "Headset (3- Jabra Evolve 20)",
            new[] { "Headset (Jabra Evolve 20)" }));
    }

    [Fact]
    public void TwoFuzzyCandidatesAreAmbiguous()
    {
        Assert.Equal(-1, MicMatch.Find("Microphone (TONOR TC30 Audio De", new[]
        {
            "Microphone (2- TONOR TC30 Audio",
            "Microphone (3- TONOR TC30 Audio",
        }));
    }

    [Fact]
    public void UntruncatedSharedPrefixDoesNotMatch()
    {
        Assert.Equal(-1, MicMatch.Find("Microphone (USB)", new[] { "Microphone (USB Audio)" }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BlankStoredNameMatchesNothing(string? stored)
    {
        Assert.Equal(-1, MicMatch.Find(stored, new[] { "Headset (Jabra Evolve 20)" }));
    }

    [Fact]
    public void UnknownNameMatchesNothing()
    {
        Assert.Equal(-1, MicMatch.Find("Line In (Internal)", new[] { "Headset (Jabra Evolve 20)" }));
    }

    [Fact]
    public void NormalizeDropsInstancePrefix()
    {
        Assert.Equal("Mikrofon (Foo)", MicMatch.Normalize("Mikrofon (12- Foo)"));
    }
}
