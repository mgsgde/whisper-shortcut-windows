using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of GermanNumberWordsTests.swift (Mac #462). The glossary-casing split is not ported.</summary>
public class GermanNumberWordsTests
{
    private static string Digits(string text) => GermanNumberWords.DigitizingQuantities(text);

    [Fact]
    public void CardinalWordsParseOtherWordsDoNot()
    {
        (string Word, int? Expected)[] cases =
        [
            ("null", 0), ("eins", 1), ("zwei", 2), ("Zehn", 10), ("elf", 11), ("zwölf", 12),
            ("sechzehn", 16), ("siebzehn", 17), ("zwanzig", 20), ("dreißig", 30), ("dreissig", 30),
            ("fünfzig", 50), ("fuenfzig", 50), ("neunzig", 90), ("einundzwanzig", 21),
            ("neunundneunzig", 99), ("hundert", 100), ("einhundert", 100), ("hundertvierzig", 140),
            ("fünfhundert", 500), ("hundertundzehn", 110), ("hunderteins", 101),
            ("zweihundertdreiundvierzig", 243),
            ("ein", null), ("eine", null), ("einer", null), ("dreiß", null), ("Hunderte", null),
            ("hundertund", null), ("Dreier", null), ("Punkte", null), ("undzwanzig", null),
        ];
        foreach (var (word, expected) in cases)
            Assert.Equal(expected, GermanNumberWords.Value(word));
    }

    [Fact]
    public void AUnitOfMeasureAfterTheWordMakesItDigits()
    {
        Assert.Equal("Gehstrecke unter 500 Meter.", Digits("Gehstrecke unter fünfhundert Meter."));
        Assert.Equal("Ramipril 5 mg", Digits("Ramipril fünf mg"));
        Assert.Equal("Rotation 60 Grad", Digits("Rotation sechzig Grad"));
        Assert.Equal("zu 90 Prozent", Digits("zu neunzig Prozent"));
        Assert.Equal("20 % weniger", Digits("zwanzig % weniger"));
        Assert.Equal("40° Beugung", Digits("vierzig° Beugung"));
    }

    [Fact]
    public void TimeUnitsAreDigitsAboveTwelveOnly()
    {
        Assert.Equal("Um zehn Uhr kam die Patientin", Digits("Um zehn Uhr kam die Patientin"));
        Assert.Equal("seit drei Tagen", Digits("seit drei Tagen"));
        Assert.Equal("in drei Monaten", Digits("in drei Monaten"));
        Assert.Equal("um 13 Uhr", Digits("um dreizehn Uhr"));
        Assert.Equal("seit 20 Jahren", Digits("seit zwanzig Jahren"));
    }

    [Fact]
    public void GdBValuesAreDigits()
    {
        Assert.Equal("Gesamt GdB 50, Merkzeichen G", Digits("Gesamt GdB fünfzig, Merkzeichen G"));
        Assert.Equal("ist ein GdB von 30 angemessen.", Digits("ist ein GdB von dreißig angemessen."));
        Assert.Equal("Einzel GdB Psyche 20.", Digits("Einzel GdB Psyche zwanzig."));
        Assert.Equal("Einzel-GdB 10", Digits("Einzel-GdB zehn"));
        Assert.Equal("GDB 40", Digits("GDB vierzig"));
        Assert.Equal("GdB für drei Funktionssysteme", Digits("GdB für drei Funktionssysteme"));
        Assert.Equal("Psyche zwanzig", Digits("Psyche zwanzig"));
    }

    [Fact]
    public void EitherSideOfZu()
    {
        Assert.Equal("Blutdruck 140 zu 90", Digits("Blutdruck 140 zu neunzig"));
        Assert.Equal("Blutdruck 140 zu 90", Digits("Blutdruck hundertvierzig zu 90"));
        Assert.Equal("Blutdruck 140 zu 90", Digits("Blutdruck hundertvierzig zu neunzig"));
        Assert.Equal("eins zu eins umsetzen", Digits("eins zu eins umsetzen"));
        Assert.Equal("zehn zu spät", Digits("zehn zu spät"));
    }

    [Fact]
    public void DigitsRightNextToIt()
    {
        Assert.Equal("Lendenwirbel 4/5", Digits("Lendenwirbel 4/fünf"));
        Assert.Equal("Werte 140 90", Digits("Werte 140 neunzig"));
        Assert.Equal("Lendenwirbel vier/fünf", Digits("Lendenwirbel vier/fünf"));
        Assert.Equal("Punkte: 3. Drei weitere folgen", Digits("Punkte: 3. Drei weitere folgen"));
    }

    [Fact]
    public void ProseStays()
    {
        foreach (var text in new[]
        {
            "Sie hat drei Punkte erreicht.",
            "ein Meter Abstand",
            "Eine Woche später",
            "eins nach dem anderen",
            "Gesamt GdB drei Pünktchen und dann weiter",
            "Die zwei Kinder kommen",
            "Ich arbeite mal weiter damit.",
            "",
        })
            Assert.Equal(text, Digits(text));
    }
}
