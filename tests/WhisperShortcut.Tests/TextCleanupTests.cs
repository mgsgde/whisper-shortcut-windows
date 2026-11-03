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

    [Theory]
    [InlineData("with proper punctuation")]
    [InlineData("clean text with")]
    [InlineData("keep only the intended meaning")]
    [InlineData("remove filler words")]
    [InlineData("preserve correct punctuation")]
    [InlineData("numbers should be written as digits")]
    public void NormalizeReplyKeepsReplyEndingWithPromptSuffix(string suffix)
    {
        var reply = "Das ist die Antwort " + suffix;
        Assert.Equal(reply, TextCleanup.NormalizeReply(reply));
    }

    [Theory]
    [InlineData("convert speech to")]
    [InlineData("please transcribe")]
    [InlineData("transcribe this audio")]
    [InlineData("proper punctuation")]
    [InlineData("the audio says:")]
    public void NormalizeReplyKeepsReplyStartingWithPromptPrefix(string prefix)
    {
        var reply = prefix + " Text der Antwort";
        Assert.Equal(reply, TextCleanup.NormalizeReply(reply));
    }

    [Fact]
    public void NormalizeReplyStripsPreambleAndCollapsesWhitespace()
    {
        Assert.Equal("Hallo Welt", TextCleanup.NormalizeReply("**Transcription:**\nHallo Welt"));
        var input = "  Einkaufsliste:\n- Milch   und  Brot  \n    - Butter\t\tfrisch\n\n\n\n- Käse\n";
        Assert.Equal(TextCleanup.Normalize(input), TextCleanup.NormalizeReply(input));
        Assert.Equal("", TextCleanup.NormalizeReply("  \n "));
    }

    [Fact]
    public void NormalizeStillStripsPromptSuffixForTranscripts()
    {
        Assert.Equal("Hallo", TextCleanup.Normalize("Hallo remove filler words"));
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
        // 2 s -> 160: the +40 slack is what keeps a dense two-second burst.
        Assert.Equal(new string('b', 160), TextCleanup.DiscardImplausible(new string('b', 160), 2));
        Assert.Equal("", TextCleanup.DiscardImplausible(new string('b', 161), 2));
    }

    [Fact]
    public void PromptSuffixesAreStripped()
    {
        Assert.Equal("Hallo", TextCleanup.Normalize("Hallo with proper punctuation"));
        Assert.Equal("", TextCleanup.Normalize("with proper punctuation"));
        Assert.Equal("Ja", TextCleanup.Normalize("Ja remove filler words"));
    }

    [Fact]
    public void EmptyAndNullNormalizeToEmpty()
    {
        Assert.Equal("", TextCleanup.Normalize(null));
        Assert.Equal("", TextCleanup.Normalize(""));
        Assert.Equal("", TextCleanup.ForDictation(null));
        Assert.Equal("", TextCleanup.StripMarkdown(""));
        Assert.Equal("", TextCleanup.StripMarkdown(null!));
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

    [Fact]
    public void FormattingWithoutPeriod()
    {
        Assert.Equal("Hallo welt", TextCleanup.ForDictation("hallo welt", addTrailingPeriod: false));
        Assert.Equal("Hallo welt.", TextCleanup.ForDictation("hallo welt"));
    }
}
