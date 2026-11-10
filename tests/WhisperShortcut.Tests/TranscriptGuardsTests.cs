using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class TranscriptGuardsTests
{
    private static readonly string[] Glossary = { "Sabaki", "Dance", "Magnus Gödde", "EnBW" };
    private static readonly string[] TrailingGlossary = { "Joschka", "SumUp", "lemniscus", "WhisperShortcut" };

    [Fact]
    public void GlossaryEchoOnLongNearSilentChunkIsDiscarded() =>
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho("sabaki.dance", 6.3, Glossary));

    [Fact]
    public void SameWordsSurviveWhenAudioIsShortEnough() =>
        Assert.Equal("Magnus Gödde", TextCleanup.DiscardGlossaryEcho("Magnus Gödde", 1.5, Glossary));

    [Theory]
    [InlineData("Goedde ramipril.")]
    [InlineData("Ramipril, Goedde")]
    [InlineData("RAMIPRIL goedde ramipril!")]
    public void PureListOfTwoTermsIsDiscardedRegardlessOfLength(string text) =>
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho(text, 2.16, new[] { "goedde", "ramipril" }));

    [Theory]
    [InlineData("Ramipril.")]
    [InlineData("Ramipril und Goedde")]
    [InlineData("Ramipril 5 mg.")]
    public void OneTermOrAnyOtherWordIsSpeech(string text) =>
        Assert.Equal(text, TextCleanup.DiscardGlossaryEcho(text, 2.16, new[] { "goedde", "ramipril" }));

    [Fact]
    public void ShortRealSpeechIsNeverDiscarded() =>
        Assert.Equal("Ja, genau.", TextCleanup.DiscardGlossaryEcho("Ja, genau.", 8.0, Glossary));

    [Fact]
    public void GlossaryTermInSentenceIsNeverDiscarded()
    {
        var text = "Ich möchte das Sabaki Dance Feature verbessern und dann noch einmal testen.";
        Assert.Equal(text, TextCleanup.DiscardGlossaryEcho(text, 6.0, Glossary));
    }

    [Fact]
    public void EchoMatchFoldsCaseAndDiacriticsButNotOe() =>
        // "goedde" is not a diacritic fold of "Gödde", so this must survive.
        Assert.Equal("magnus goedde", TextCleanup.DiscardGlossaryEcho("magnus goedde", 20.0, new[] { "Magnus Gödde" }));

    [Fact]
    public void EmptyGlossaryDisablesEchoGate() =>
        Assert.Equal("sabaki.dance", TextCleanup.DiscardGlossaryEcho("sabaki.dance", 6.3, Array.Empty<string>()));

    [Fact]
    public void UnknownDurationDisablesEchoGate() =>
        // One term only: the pure-list rule needs no duration, the length floor does.
        Assert.Equal("Sabaki", TextCleanup.DiscardGlossaryEcho("Sabaki", 0, Glossary));

    [Fact]
    public void SingleGlossaryTermOnLongNearSilentAudioIsDiscarded() =>
        // Not a two-term list, so the length floor must fire (20 s → min 60 chars).
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho("Sabaki", 20.0, new[] { "Sabaki" }));

    [Fact]
    public void TrailingEchoReportAIsStripped()
    {
        var text = "Wir kriegen von der Personalabteilung immer Ein- und Austrittslisten, damit ausgetretene Mitarbeiter im System gesperrt werden. Joschka SumUp lemniscus";
        Assert.Equal("Wir kriegen von der Personalabteilung immer Ein- und Austrittslisten, damit ausgetretene Mitarbeiter im System gesperrt werden.",
            TextCleanup.StripTrailingGlossaryEcho(text, TrailingGlossary));
    }

    [Fact]
    public void TrailingEchoReportBIsStripped()
    {
        var text = "Ein Kunde Medizintechnik Bartsch hat eine Anfrage an mich geschickt. Hintergrund, ich habe Im Flyer den Flyer für unsere Spracherkennung WhisperShortcut zugeschickt. Joschka, SumUp, lemniscus, WhisperShortcut.";
        Assert.Equal("Ein Kunde Medizintechnik Bartsch hat eine Anfrage an mich geschickt. Hintergrund, ich habe Im Flyer den Flyer für unsere Spracherkennung WhisperShortcut zugeschickt.",
            TextCleanup.StripTrailingGlossaryEcho(text, TrailingGlossary));
    }

    [Theory]
    [InlineData("Das war's. SumUp.")]                              // single term
    [InlineData("Das war's. WhisperShortcut Joschka.")]            // out of order
    [InlineData("Das war's. Wir nutzen SumUp und lemniscus.")]     // non-glossary words
    [InlineData("Teilnehmer: Joschka, SumUp")]                     // colon is no boundary
    [InlineData("Joschka SumUp")]                                  // no boundary at all
    public void TrailingEchoNonMatchesStay(string text) =>
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, TrailingGlossary));

    // Mac b64a814b (#436): the real 49-term glossary and the echo a cloud model read back in keywords order.
    private static readonly string[] RealGlossaryTerms =
    {
        "Claude", "Christoph", "Michelbach", "Cursor", "EnBW", "Grok", "GrokBot", "Opus", "Sonnet", "Gemini",
        "Implementer Queue", "Karlsruhe", "Kizomba", "Salsa", "AutoMagic", "Onepager", "Whisper Shortcut",
        "Schönstein", "BlockInfinity", "Claude Code", "MCP", "Bachata", "sabaki.dance", "Wianco", "Datev",
        "Regulon", "Lemniskus", "Parakeet", "Vibe-Coding", "KMU", "Odoo", "Johanna", "Pia", "Codex",
        "Wellensteyn", "Gödde", "Seiter", "Anthropic", "Azure DevOps", "Läuffer", "cloud session",
        "Frau Dr. Läuffer", "Dr. Läuffer", "Frau Läuffer", "Dependabot", "Joschka", "Ramin", "Baileys", "Vattenfall",
    };

    private const string KeywordOrderEcho = "Claude Code MCP Bachata sabaki.dance Wianco Datev Regulon Lemniskus Parakeet Vibe-Coding KMU Odoo Johanna Pia Codex Wellensteyn Gödde Läuffer Frau Dr. Läuffer Dr. Läuffer Frau Läuffer Dependabot Joschka Ramin Baileys Vattenfall Claude Christoph Michelbach Cursor EnBW Grok GrokBot Opus Sonnet Gemini Implementer Queue Karlsruhe Kizomba Salsa AutoMagic Onepager Whisper Shortcut Schönstein BlockInfinity Seiter Anthropic Azure DevOps cloud session";

    [Fact]
    public void KeywordOrderEchoIsDiscarded() =>
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho(KeywordOrderEcho + ".", 11.0, RealGlossaryTerms));

    [Fact]
    public void KeywordOrderEchoAfterRealSpeechIsStripped()
    {
        var head = "Ah, was da natürlich noch cool wäre, wenn die aufgeladen ist. Das wäre vielleicht noch cool.";
        Assert.Equal(head, TextCleanup.StripTrailingGlossaryEcho(head + " " + KeywordOrderEcho + ".", RealGlossaryTerms));
    }

    [Fact]
    public void ShortShuffledListAfterASentenceStays()
    {
        var text = "Wir haben heute verglichen. Opus Grok Sonnet Claude Gemini Cursor.";
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, RealGlossaryTerms));
    }

    [Fact]
    public void ConnectorInsideTrailingPairStays()
    {
        var text = "Das war's. SumUp und lemniscus.";
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, TrailingGlossary));
    }

    private static readonly string[] ConnectorGlossary = { "Claude Code", "MCP", "Bachata" };

    // Mac 63ded84b (#261): connectors between glossary terms are neutral.
    [Fact]
    public void ConnectorsBetweenTermsStillFormAGlossaryList()
    {
        const string text = "Claude Code, MCP und Bachata.";
        Assert.True(TextCleanup.IsPureGlossaryList(text, ConnectorGlossary));
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho(text, 20.0, ConnectorGlossary));
    }

    [Fact]
    public void ConnectorsAloneAreNoGlossaryList()
    {
        Assert.False(TextCleanup.IsPureGlossaryList("und oder mit", ConnectorGlossary));
        Assert.Equal("und oder mit", TextCleanup.DiscardGlossaryEcho("und oder mit", 20.0, ConnectorGlossary));
    }

    [Fact]
    public void ShortTextRuleIgnoresConnectors() =>
        Assert.Equal("", TextCleanup.DiscardGlossaryEcho("Claude Code und MCP und Bachata", 20.0, ConnectorGlossary));

    [Fact]
    public void ConnectorWithNonGlossaryWordIsSpeech()
    {
        const string text = "MCP und Bachata morgen";
        Assert.False(TextCleanup.IsPureGlossaryList(text, ConnectorGlossary));
    }

    // Mac 34bb2ce4: a lone trailing term goes after a head of 5+ words, and only as exactly that term.
    [Fact]
    public void LoneTrailingTermIsStrippedAfterLongHead() =>
        Assert.Equal("Das ist ein 14 Zoll Notebook.",
            TextCleanup.StripTrailingGlossaryEcho("Das ist ein 14 Zoll Notebook. Joschka.", TrailingGlossary));

    [Fact]
    public void LoneMultiWordTermIsStrippedWhole() =>
        Assert.Equal("Das ist ein 14 Zoll Notebook.",
            TextCleanup.StripTrailingGlossaryEcho("Das ist ein 14 Zoll Notebook. Claude Code.", ConnectorGlossary));

    [Theory]
    [InlineData("Das war es. Joschka.")]                                   // head under 5 words
    [InlineData("Das ist ein 14 Zoll Notebook. Viele Grüße Joschka.")]     // extra words beyond the term
    [InlineData("Das ist ein 14 Zoll Notebook. Joschka Joschka.")]         // same term repeated
    [InlineData("Das ist ein 14 Zoll Notebook. Code.")]                    // part of a two-word term only
    public void LoneTrailingTermNonMatchesStay(string text) =>
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, TrailingGlossary.Concat(ConnectorGlossary).ToArray()));

    [Fact]
    public void DottedTokenIsNoBoundary()
    {
        const string text = "sabaki.dance";
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, new[] { "Sabaki", "Dance" }));
    }

    [Fact]
    public void EmptyGlossaryDisablesTrailingStrip()
    {
        const string text = "Hallo. Joschka SumUp";
        Assert.Equal(text, TextCleanup.StripTrailingGlossaryEcho(text, Array.Empty<string>()));
    }

    [Fact]
    public void NewlineIsABoundary() =>
        Assert.Equal("Erster Absatz.", TextCleanup.StripTrailingGlossaryEcho("Erster Absatz.\nJoschka, SumUp", TrailingGlossary));

    [Theory]
    [InlineData("Bitte geben Sie mir die Audiodatei, den ich transkribieren soll.")]
    [InlineData("Please provide the audio file.")]
    [InlineData("I can transcribe that for you.")]
    [InlineData("transcription: hello there")]
    [InlineData("Here is the transcription")]
    [InlineData("context: something")]
    [InlineData("Convert speech to text, remove filler words, with proper punctuation.")]
    public void RefusalsAndPromptLeaksAreDetected(string text) => Assert.True(TextCleanup.LooksLikeRefusal(text));

    [Fact]
    public void FullDefaultPromptIsALeak() =>
        Assert.True(TextCleanup.LooksLikeRefusal("Sure. " + Prompts.DefaultTranscriptionSystemPrompt.Trim()));

    [Theory]
    [InlineData("Please transcribe this meeting for Sara tomorrow at three.")]
    [InlineData("The meeting notes mention transcription: we should keep a written record.")]
    [InlineData("Ja, genau.")]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalTranscriptsAreNotRefusals(string text) => Assert.False(TextCleanup.LooksLikeRefusal(text));

    [Fact]
    public void LongTranscriptContainingRefusalPhraseIsKept()
    {
        var text = string.Concat(Enumerable.Repeat("We discussed the budget and next steps. ", 8)) + "I can transcribe those notes later if needed.";
        Assert.True(text.Length >= 160);
        Assert.False(TextCleanup.LooksLikeRefusal(text));
    }
}
