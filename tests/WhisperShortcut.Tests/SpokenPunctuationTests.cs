using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of SpokenPunctuationTests.swift: what changes, and the ordinary words that must not.</summary>
public class SpokenPunctuationTests
{
    private static string A(string s) => SpokenPunctuation.Apply(s);

    [Fact]
    public void ColonAttachesToPreviousWord() =>
        Assert.Equal("Anamnese: Patient berichtet über Schmerzen",
            A("Anamnese Doppelpunkt Patient berichtet über Schmerzen"));

    [Fact]
    public void ColonEatsStrayPunctuation()
    {
        Assert.Equal("Befund: Inspektion", A("Befund, Doppelpunkt. Inspektion"));
        Assert.Equal("Befund: Inspektion", A("Befund. Doppelpunkt, Inspektion"));
        Assert.Equal("Therapie:", A("Therapie Doppelpunkt."));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal("Befund: Inspektion", A("Befund doppelpunkt Inspektion"));
        Assert.Equal("Befund: Inspektion", A("Befund DOPPELPUNKT Inspektion"));
    }

    [Fact]
    public void ParagraphBreak()
    {
        Assert.Equal("Patient klagt über Schmerzen.\n\nBefund unauffällig",
            A("Patient klagt über Schmerzen, neuer Absatz, Befund unauffällig"));
        Assert.Equal("Patient klagt über Schmerzen.\n\nBefund unauffällig",
            A("Patient klagt über Schmerzen. Neuer Absatz. Befund unauffällig"));
    }

    [Fact]
    public void LineBreak() =>
        Assert.Equal("Erste Zeile\nzweite Zeile", A("Erste Zeile neue Zeile zweite Zeile"));

    [Fact]
    public void FullNote()
    {
        var spoken = "Anamnese Doppelpunkt Schmerzen LWS seit drei Tagen. Neuer Absatz Befund, Doppelpunkt. "
            + "ISG links blockiert. Neuer Absatz. Therapie Doppelpunkt neue Zeile HVLA";
        Assert.Equal("Anamnese: Schmerzen LWS seit drei Tagen.\n\nBefund: ISG links blockiert.\n\nTherapie:\nHVLA", A(spoken));
    }

    [Fact]
    public void OrdinaryWordsStay()
    {
        var text = "Der wichtigste Punkt ist das Komma und das Fragezeichen am Ende.";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void WholeWordsOnly()
    {
        var text = "Die Doppelpunkte und der Absatzmarke bleiben.";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void NoCommandsUnchangedWhitespaceIncluded()
    {
        var text = "  Patient berichtet über Schmerzen im unteren Rücken.\n";
        Assert.Equal(text, A(text));
    }

    [Fact]
    public void HyphenatedWords()
    {
        Assert.Equal("Anamnese: LWS-Schmerzen seit drei Wochen.",
            A("Anamnese Doppelpunkt LWS-Schmerzen seit drei Wochen."));
        Assert.Equal("Anamnese: Patient berichtet", A("Anamnese, Doppelpunkt. Patient berichtet"));
    }

    [Fact]
    public void Sequence()
    {
        Assert.Equal("Anamnese: Schmerzen.\n\nBefund: Tonus erhöht",
            A("Anamnese Doppelpunkt Schmerzen. Neuer Absatz. Befund Doppelpunkt Tonus erhöht"));
        Assert.Equal("Erstens\nzweitens", A("Erstens neue Zeile zweitens"));
        Assert.Equal("Doppelpunkte zählen", A("Doppelpunkte zählen"));
    }

    [Fact]
    public void GluedColon()
    {
        var raw = "Anamnese Doppelpunkt Schmerzen im unteren Rücken seit drei Wochen, neuer Absatz, "
            + "Befund Doppelpunktdruckschmerz L4 bis S1, neuer Absatz, "
            + "Therapie Doppelpunkt Mobilisation der LWS, das ist der wichtigste Punkt.";
        Assert.Equal("Anamnese: Schmerzen im unteren Rücken seit drei Wochen.\n\nBefund: Druckschmerz L4 bis S1.\n\n"
            + "Therapie: Mobilisation der LWS, das ist der wichtigste Punkt.", A(raw));
        var inflected = "Die Doppelpunkten und des Doppelpunktes";
        Assert.Equal(inflected, A(inflected));
    }
}
