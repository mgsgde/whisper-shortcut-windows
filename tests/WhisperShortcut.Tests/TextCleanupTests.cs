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

public class StripMarkdownTests
{
    [Theory]
    [InlineData("## Befund", "Befund:")]
    [InlineData("# Therapie:", "Therapie:")]
    [InlineData("###### Tief", "Tief:")]
    [InlineData("**Befund:**", "Befund:")]
    [InlineData("Das ist **wichtig** und __auch__ das", "Das ist wichtig und auch das")]
    [InlineData("* Punkt", "- Punkt")]
    [InlineData("  + Unterpunkt", "  - Unterpunkt")]
    [InlineData("- schon gut", "- schon gut")]
    [InlineData("2*3 = 6 und a*b", "2*3 = 6 und a*b")]
    [InlineData("siehe https://example.com/a_b__c/x", "siehe https://example.com/a_b__c/x")]
    public void Lines(string input, string expected) =>
        Assert.Equal(expected, TextCleanup.StripMarkdown(input));

    [Fact]
    public void CodeFences_AreRemoved() =>
        Assert.Equal("a\nb", TextCleanup.StripMarkdown("```\na\n```\nb"));

    [Fact]
    public void MixedExample()
    {
        var input = "## Anamnese\n* Nackenschmerzen seit **5 Tagen**\n\n**Befund:**\n+ C5/C6 links eingeschränkt\n\n### Therapie\n- (nicht diktiert)";
        var expected = "Anamnese:\n- Nackenschmerzen seit 5 Tagen\n\nBefund:\n- C5/C6 links eingeschränkt\n\nTherapie:\n- (nicht diktiert)";
        Assert.Equal(expected, TextCleanup.StripMarkdown(input));
    }
}
