using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class TextCleanupTests
{
    [Fact]
    public void LineBreaksAndIndentedBulletsSurvive()
    {
        var input = "  Einkaufsliste:\n- Milch   und  Brot  \n    - Butter\t\tfrisch\n\n\n\n- Käse\n";
        var expected = "Einkaufsliste:\n- Milch und Brot\n    - Butter frisch\n\n- Käse";
        Assert.Equal(expected, TextCleanup.Normalize(input));
    }

    [Fact]
    public void PreambleAndPrefixAreRemoved()
    {
        Assert.Equal("Hallo Welt", TextCleanup.Normalize("**Transcription:**\nHallo Welt"));
        Assert.Equal("Hallo Welt", TextCleanup.Normalize("Here is the transcription of the audio you provided: Hallo Welt"));
        Assert.Equal("Hallo", TextCleanup.Normalize("transcribed text: Hallo"));
        // Word boundary: a prefix inside a longer word is real speech and stays.
        Assert.Equal("transcribed texts are long", TextCleanup.Normalize("transcribed texts are long"));
    }

    [Fact]
    public void DictationGetsCapitalAndPeriod()
    {
        Assert.Equal("Das ist ein Test.", TextCleanup.ForDictation("das ist ein Test"));
        Assert.Equal("Wirklich?", TextCleanup.ForDictation("wirklich?"));
        Assert.Equal("Eins;", TextCleanup.ForDictation("eins;"));
        Assert.Equal("", TextCleanup.ForDictation("  "));
    }

    [Fact]
    public void PromptAnswerGetsNeitherCapitalNorPeriod()
    {
        Assert.Equal("hallo zusammen", TextCleanup.Normalize("  hallo   zusammen "));
    }

    [Fact]
    public void ImplausibleLengthIsDiscarded()
    {
        var ok = new string('a', 100);          // 1 s -> max 100
        Assert.Equal(ok, TextCleanup.DiscardImplausible(ok, 1));
        Assert.Equal("", TextCleanup.DiscardImplausible(ok + "a", 1));
        Assert.Equal("x", TextCleanup.DiscardImplausible("x", 0));
    }
}
