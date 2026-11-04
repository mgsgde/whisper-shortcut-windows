using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Port of ParakeetPunctuationTimingTests.swift: the spoken-vs-guessed rule on hand-built token arrays.</summary>
public class ParakeetPunctuationTimingTests
{
    private static ParakeetPunctuationTiming.Token Word(string text, double start, double end) =>
        new("▁" + text, start, end);

    private static ParakeetPunctuationTiming.Token Mark(string text, double start, double end) =>
        new(text, start, end);

    private static List<bool> Spoken(IReadOnlyList<ParakeetPunctuationTiming.Token> tokens, double duration) =>
        ParakeetPunctuationTiming.SpokenMarks(tokens, duration);

    private static ParakeetPunctuationTiming.Filtered? Filter(string text, ParakeetPunctuationTiming.Token[] tokens, double duration) =>
        ParakeetPunctuationTiming.Filter(text, tokens, duration);

    // "Gangbild , geht weiter" with a configurable pause after the comma.
    private static ParakeetPunctuationTiming.Token[] CommaTokens(double gap) =>
    [
        Word("Gangbild", 0.0, 0.6), Mark(",", 0.6, 0.64),
        Word("geht", 0.64 + gap, 0.9 + gap), Word("weiter", 0.9 + gap, 1.3 + gap),
    ];

    [Fact]
    public void CommaGap()
    {
        Assert.Equal(new[] { false }, Spoken(CommaTokens(0), 2));
        Assert.Equal(new[] { true }, Spoken(CommaTokens(0.2), 2));
    }

    private static ParakeetPunctuationTiming.Token[] PeriodTokens() =>
        [Word("Gangbild", 0.0, 0.6), Word("unauffällig", 0.6, 1.2), Mark(".", 1.2, 1.24)];

    [Fact]
    public void FinalPeriodTail()
    {
        var tokens = PeriodTokens();
        // 100 ms is under both the old 240 ms line and the current 400 ms line.
        Assert.Equal(new[] { false }, Spoken(tokens, 1.24 + 0.1));
        // 500 ms clears 400 ms. A tail of 300 ms, which the old 240 ms line kept, is now dropped.
        Assert.Equal(new[] { false }, Spoken(tokens, 1.24 + 0.30));
        Assert.Equal(new[] { true }, Spoken(tokens, 1.24 + 0.5));
        Assert.Equal(new[] { true }, Spoken(tokens, 1.24 + 0.70));
    }

    [Fact]
    public void QuestionAndExclamation()
    {
        // Gap after "?" is 0.96 s, tail after "!" is 3.16 s: both spoken.
        var tokens = new[] { Word("Stimmt", 0, 0.5), Mark("?", 0.5, 0.54), Word("Ja", 1.5, 1.8), Mark("!", 1.8, 1.84) };
        Assert.Equal(new[] { true, true }, Spoken(tokens, 5));
    }

    [Fact]
    public void DecimalPointIsNotAMark()
    {
        var tokens = new[] { Word("Wert", 0, 0.4), Word("3", 0.4, 0.6), Mark(".", 0.6, 0.62), new ParakeetPunctuationTiming.Token("5", 0.62, 0.8) };
        Assert.Empty(Spoken(tokens, 1));
    }

    [Fact]
    public void FilterComma()
    {
        var guessed = Filter("Gangbild, geht weiter", CommaTokens(0), 2);
        Assert.Equal(new ParakeetPunctuationTiming.Filtered("Gangbild geht weiter", 0, 1), guessed);
        var spoken = Filter("Gangbild, geht weiter", CommaTokens(0.2), 2);
        Assert.Equal(new ParakeetPunctuationTiming.Filtered("Gangbild, geht weiter", 1, 0), spoken);
    }

    [Fact]
    public void FilterFinalPeriod()
    {
        var tokens = PeriodTokens();
        Assert.Equal("Gangbild unauffällig", Filter("Gangbild unauffällig.", tokens, 1.34)?.Text);
        Assert.Equal("Gangbild unauffällig.", Filter("Gangbild unauffällig.", tokens, 1.8)?.Text);
    }

    [Fact]
    public void LowersFunctionWord()
    {
        var tokens = new[]
        {
            Word("Wir", 0, 0.3), Word("gehen", 0.3, 0.6), Mark(".", 0.6, 0.62), Word("Und", 0.62, 0.8),
            Word("Befund", 0.8, 1.2), Mark(".", 1.2, 1.22), Word("Knie", 1.22, 1.5),
        };
        var result = Filter("Wir gehen. Und Befund. Knie", tokens, 3);
        Assert.Equal("Wir gehen und Befund Knie", result?.Text);
        Assert.Equal(2, result?.Dropped);
    }

    [Fact]
    public void LowersAdverbNounKeepsCapital()
    {
        var tokens = new[]
        {
            Word("Ich", 0, 0.2), Word("arbeite", 0.2, 0.5), Word("mal", 0.5, 0.7), Mark(".", 0.7, 0.72),
            Word("Weiter", 0.72, 1.0), Word("damit", 1.0, 1.3),
        };
        Assert.Equal("Ich arbeite mal weiter damit", Filter("Ich arbeite mal. Weiter damit", tokens, 3)?.Text);
        var noun = new[]
        {
            Word("Befund", 0, 0.4), Mark(".", 0.4, 0.42), Word("Patientin", 0.42, 0.9),
            Word("kam", 0.9, 1.1),
        };
        Assert.Equal("Befund Patientin kam", Filter("Befund. Patientin kam", noun, 3)?.Text);
    }

    [Fact]
    public void KeepsOrdinal()
    {
        var tokens = new[] { Word("5", 0, 0.3), Mark(".", 0.3, 0.32), Word("Lendenwirbel", 0.32, 1.0) };
        Assert.Equal("5. Lendenwirbel", Filter("5. Lendenwirbel", tokens, 2)?.Text);
    }

    [Fact]
    public void CountMismatchReturnsNull() =>
        Assert.Null(Filter("Gangbild, geht, weiter", CommaTokens(0), 2));

    [Fact]
    public void QuestionMarkTail()
    {
        var tokens = new[] { Word("Stimmt", 0, 0.5), Mark("?", 0.5, 0.54) };
        Assert.Equal("Stimmt", Filter("Stimmt?", tokens, 0.54 + 0.10)?.Text);
        Assert.Equal("Stimmt?", Filter("Stimmt?", tokens, 0.54 + 0.70)?.Text);
    }

    [Fact]
    public void StripTimedMarks()
    {
        Assert.Equal("Hallo, Welt ok? Gut.",
            SpokenPunctuation.StripModelPunctuation("Hallo, Welt; ok? Gut.", timedMarks: true));
        // No timings: the model's "!" is still dropped, so a period written beside it is what remains.
        // With timings the "!" the filter kept is left alone.
        Assert.Equal("Wirklich.", SpokenPunctuation.StripModelPunctuation("Wirklich.!"));
        Assert.Equal("Wirklich.!", SpokenPunctuation.StripModelPunctuation("Wirklich.!", timedMarks: true));
    }

    // "Gesamt GdB 50,Merkzeichen G." with Parakeet's comma glued to the next word.
    private static ParakeetPunctuationTiming.Token[] GluedTokens(double gap) =>
    [
        Word("Gesamt", 0, 0.4), Word("GdB", 0.4, 0.8), Word("50", 0.8, 1.2), Mark(",", 1.2, 1.24),
        Mark("Merk", 1.24 + gap, 1.5 + gap), Mark("zeichen", 1.5 + gap, 1.8 + gap),
        Word("G", 1.8 + gap, 2.0 + gap), Mark(".", 2.0 + gap, 2.04 + gap),
    ];

    [Fact]
    public void GluedCommaKept()
    {
        var tokens = GluedTokens(0.2);
        Assert.Equal(new[] { true, true }, Spoken(tokens, 4));
        var result = Filter("Gesamt GdB 50,Merkzeichen G.", tokens, 4);
        Assert.Equal(new ParakeetPunctuationTiming.Filtered("Gesamt GdB 50, Merkzeichen G.", 2, 0), result);
    }

    [Fact]
    public void GluedCommaDropped()
    {
        var result = Filter("Gesamt GdB 50,Merkzeichen G.", GluedTokens(0), 4);
        Assert.Equal("Gesamt GdB 50 Merkzeichen G.", result?.Text);
        Assert.Equal(1, result?.Dropped);
    }

    [Fact]
    public void DecimalCommaStaysUntouched()
    {
        var tokens = new[]
        {
            Word("Wert", 0, 0.4), Word("3", 0.4, 0.6), Mark(",", 0.6, 0.62),
            new ParakeetPunctuationTiming.Token("5", 0.62, 0.8), Word("mg", 0.8, 1.0), Mark(".", 1.0, 1.04),
        };
        Assert.Equal("Wert 3,5 mg.", Filter("Wert 3,5 mg.", tokens, 3)?.Text);
    }

    [Fact]
    public void SpacingGluedCommasBeforeLetterOnly() =>
        Assert.Equal("50, Merkzeichen und 3,5", ParakeetPunctuationTiming.SpacingGluedCommas("50,Merkzeichen und 3,5"));

    // "Stimmt ? Ja" with a configurable pause after the question mark.
    private static ParakeetPunctuationTiming.Token[] QuestionTokens(double gap) =>
    [
        Word("Stimmt", 0.0, 0.5), Mark("?", 0.5, 0.54),
        Word("Ja", 0.54 + gap, 0.8 + gap),
    ];

    [Fact]
    public void QuestionGap()
    {
        Assert.Equal(new[] { false }, Spoken(QuestionTokens(0), 2));
        Assert.Equal(new[] { true }, Spoken(QuestionTokens(0.20), 2));
        Assert.Equal("Stimmt Ja", Filter("Stimmt? Ja", QuestionTokens(0), 2)?.Text);
        Assert.Equal("Stimmt? Ja", Filter("Stimmt? Ja", QuestionTokens(0.20), 2)?.Text);
    }

    [Fact]
    public void ExclamationTail()
    {
        var tokens = new[] { Word("Wirklich", 0.0, 0.6), Mark("!", 0.6, 0.64) };
        Assert.Equal(new[] { false }, Spoken(tokens, 0.64 + 0.10));
        Assert.Equal(new[] { true }, Spoken(tokens, 0.64 + 0.70));
        Assert.Equal("Wirklich", Filter("Wirklich!", tokens, 0.64 + 0.10)?.Text);
        Assert.Equal("Wirklich!", Filter("Wirklich!", tokens, 0.64 + 0.70)?.Text);
    }

    [Fact]
    public void SpokenExclamationSurvivesFilterAndStrip()
    {
        var tokens = new[] { Word("Wirklich", 0.0, 0.6), Mark("!", 0.6, 0.64) };
        var filtered = Filter("Wirklich!", tokens, 0.64 + 0.70);
        Assert.Equal("Wirklich!", filtered?.Text);
        // A period glued in front of the spoken "!": timing keeps the "!" (final mark, with a tail).
        var both = new[] { Word("Wirklich", 0.0, 0.6), Mark(".", 0.6, 0.64), Mark("!", 0.64, 0.68) };
        var kept = Filter("Wirklich.!", both, 0.68 + 0.70);
        Assert.Equal("Wirklich.!", kept?.Text);
        Assert.Equal("Wirklich.!", SpokenPunctuation.StripModelPunctuation(kept!.Text, timedMarks: true));
    }

    // ---- KeepSpokenMarks (the app-side entry point) ----

    [Fact]
    public void KeepSpokenMarksUsesTimingWhenTokensLineUp()
    {
        var text = ParakeetPunctuationTiming.KeepSpokenMarks("Gangbild, geht weiter", CommaTokens(0), 2, out var timed);
        Assert.True(timed);
        Assert.Equal("Gangbild geht weiter", text);
    }

    [Fact]
    public void KeepSpokenMarksFallsBackWithoutTokens()
    {
        var text = ParakeetPunctuationTiming.KeepSpokenMarks("50,Merkzeichen, ok", null, 2, out var timed);
        Assert.False(timed);
        Assert.Equal("50, Merkzeichen, ok", text);
    }

    [Fact]
    public void KeepSpokenMarksFallsBackOnCountMismatch()
    {
        var text = ParakeetPunctuationTiming.KeepSpokenMarks("Gangbild, geht, weiter", CommaTokens(0), 2, out var timed);
        Assert.False(timed);
        Assert.Equal("Gangbild, geht, weiter", text);
    }

    [Fact]
    public void TokensWithLeadingSpaceStartAWord()
    {
        // sherpa-onnx may hand out " geht" instead of the SentencePiece marker.
        var tokens = new[]
        {
            new ParakeetPunctuationTiming.Token(" Gangbild", 0.0, 0.6), new ParakeetPunctuationTiming.Token(",", 0.6, 0.64),
            new ParakeetPunctuationTiming.Token(" geht", 0.64, 0.9),
        };
        Assert.Equal(new[] { false }, Spoken(tokens, 2));
    }
}
