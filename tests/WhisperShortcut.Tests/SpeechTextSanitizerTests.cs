using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SpeechTextSanitizerTests
{
    [Theory]
    [InlineData("Das ist ein ganz normaler Absatz. Er hat zwei Sätze und liest sich flüssig.")]
    [InlineData("Heute war ein schöner Tag, und wir sind spazieren gegangen. Danach gab es Kuchen!")]
    [InlineData("\"Kommst du heute Abend?\", fragte sie. Ich wusste es noch nicht.")]
    [InlineData("Eine Zeile mit Umlauten: schön, größer, Übung. Fertig.")]
    public void Prose_IsRecognised(string text) => Assert.True(SpeechTextSanitizer.LooksLikePlainProse(text));

    [Fact]
    public void Prose_WithWindowsLineBreaks_IsStillProse() =>
        Assert.True(SpeechTextSanitizer.LooksLikePlainProse("Erster Absatz mit einem Satz.\r\n\r\nZweiter Absatz, ebenfalls ein Satz."));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```csharp\nvar x = 1;\n```")]
    [InlineData("Rufe `git status` auf. Dann geht es weiter.")]
    [InlineData("# Titel\nEin Satz darunter.")]
    [InlineData("- erster Punkt.\n- zweiter Punkt.")]
    [InlineData("1. Erstens. 2. Zweitens.")]
    [InlineData("| a | b |\n| 1 | 2 |")]
    [InlineData("Das ist **wichtig**. Wirklich.")]
    [InlineData("Siehe [die Doku](https://example.com/doku) dazu. Danke.")]
    [InlineData("Mehr unter https://example.com/seite und dann weiter.")]
    [InlineData("Mehr unter www.example.com und dann weiter.")]
    [InlineData("Die Datei liegt in /usr/local/bin/tool. Starte sie.")]
    [InlineData("if (x) { return 1; }")]
    [InlineData("Der Aufruf foo() liefert null. Das ist alles.")]
    [InlineData("2026-11-03T10:00:00 Fehler beim Start. Abbruch.")]
    [InlineData("12:30:45 Server gestartet. Alles gut.")]
    [InlineData("Ein Satz ohne Ende und ohne Punkt")]
    [InlineData("1234 5678 9012 3456 7890 1234 5678.")]
    public void NotProse_IsRejected(string text) => Assert.False(SpeechTextSanitizer.LooksLikePlainProse(text));

    [Fact]
    public void PlainSpeech_KeepsLinkLabel_DropsTarget() =>
        Assert.Equal("Siehe die Doku dazu.", SpeechTextSanitizer.PlainSpeech("Siehe [die Doku](https://example.com/doku) dazu."));

    [Fact]
    public void PlainSpeech_DropsCitationLinksAndMarkers()
    {
        Assert.Equal("Das stimmt.", SpeechTextSanitizer.PlainSpeech("Das stimmt.[[1]](https://example.com/a)"));
        Assert.Equal("Das stimmt.", SpeechTextSanitizer.PlainSpeech("Das stimmt.[2](https://example.com/b)"));
        Assert.Equal("Das stimmt.", SpeechTextSanitizer.PlainSpeech("Das stimmt.[web:9]"));
        Assert.Equal("Das stimmt.", SpeechTextSanitizer.PlainSpeech("Das stimmt.[1]"));
    }

    [Fact]
    public void PlainSpeech_DropsImagesAndBareUrls()
    {
        Assert.Equal("Vorher Nachher", SpeechTextSanitizer.PlainSpeech("Vorher ![Bild](bild.png) Nachher"));
        Assert.Equal("Mehr dazu auf  und fertig.".Replace("  ", " "),
            SpeechTextSanitizer.PlainSpeech("Mehr dazu auf https://example.com/x?y=1 und fertig."));
        Assert.Equal("Schau  an.".Replace("  ", " "), SpeechTextSanitizer.PlainSpeech("Schau <https://example.com> an."));
    }

    [Fact]
    public void PlainSpeech_RemovesEmphasisMarkers_KeepsTheWords()
    {
        Assert.Equal("Das ist wichtig und kursiv und durchgestrichen und Code.",
            SpeechTextSanitizer.PlainSpeech("Das ist **wichtig** und *kursiv* und ~~durchgestrichen~~ und `Code`."));
        Assert.Equal("Auch fett und schräg.", SpeechTextSanitizer.PlainSpeech("Auch __fett__ und _schräg_."));
        // snake_case words are not emphasis.
        Assert.Equal("Der Wert my_var_name bleibt.", SpeechTextSanitizer.PlainSpeech("Der Wert my_var_name bleibt."));
    }

    [Fact]
    public void PlainSpeech_StripsHeadingsListsAndQuotes()
    {
        var md = "# Überschrift\n\n- eins\n- zwei\n* drei\n1. vier\n2) fünf\n\n> zitiert";
        Assert.Equal("Überschrift\n\neins\nzwei\ndrei\nvier\nfünf\n\nzitiert", SpeechTextSanitizer.PlainSpeech(md));
    }

    [Fact]
    public void PlainSpeech_DropsFencedCode_AlsoUnterminated()
    {
        var md = "Vorher.\n\n```bash\nrm -rf /\necho hi\n```\n\nNachher.";
        Assert.Equal("Vorher.\n\nNachher.", SpeechTextSanitizer.PlainSpeech(md));
        Assert.Equal("Vorher.", SpeechTextSanitizer.PlainSpeech("Vorher.\n```python\nprint(1)\n"));
    }

    [Fact]
    public void PlainSpeech_TurnsTablesIntoPauses_AndDropsRules()
    {
        var md = "| Name | Alter |\n|------|:-----:|\n| Anna | 30 |\n\n---\n\nEnde.";
        // Like the Mac: the separator row and the rule leave blank lines, each row's closing pipe becomes a trailing pause.
        Assert.Equal("Name, Alter,\n\nAnna, 30,\n\nEnde.", SpeechTextSanitizer.PlainSpeech(md));
    }

    [Fact]
    public void PlainSpeech_CollapsesGaps_AndDropsPunctuationOnlyLines()
    {
        Assert.Equal("a b", SpeechTextSanitizer.PlainSpeech("a   b"));
        Assert.Equal("eins\n\nzwei", SpeechTextSanitizer.PlainSpeech("eins\n\n\n\n\nzwei"));
        // A source list that was all links leaves ", ," lines behind.
        Assert.Equal("Quellen:", SpeechTextSanitizer.PlainSpeech("Quellen:\n[1](https://a.example)\n.\n"));
    }

    [Fact]
    public void PlainSpeech_HandlesWindowsLineBreaks() =>
        Assert.Equal("Titel\n\nText", SpeechTextSanitizer.PlainSpeech("# Titel\r\n\r\nText  \r\n"));

    [Fact]
    public void PlainSpeech_PlainTextStaysAsItIs()
    {
        var text = "Ein ganz normaler Satz. Und noch einer, mit Komma.";
        Assert.Equal(text, SpeechTextSanitizer.PlainSpeech(text));
        Assert.Equal("", SpeechTextSanitizer.PlainSpeech("   \n"));
    }
}
