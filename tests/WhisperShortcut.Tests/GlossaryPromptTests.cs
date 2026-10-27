using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class GlossaryPromptTests
{
    private static string[] L(params string[] lines) => lines;

    [Fact]
    public void RejectedSpellingIsNotReferenceVocabulary()
    {
        var parsed = GlossaryPrompt.Parse(L("Bachata (not \"Batchata\")", "Batchata", "Ramipril (nicht \"Ramepril\")"));
        Assert.Equal(new[] { "Bachata", "Ramipril" }, parsed.Terms);
        Assert.Equal(2, parsed.Corrections.Count);
        Assert.Equal("Bachata", parsed.Corrections[0].Correct);
        Assert.Equal(new[] { "Batchata" }, parsed.Corrections[0].Rejected);
    }

    [Fact]
    public void AnnotatedSpellingWinsTheFoldDedupe()
    {
        var parsed = GlossaryPrompt.Parse(L("bachata", "Bachata (not \"Batchata\")"));
        Assert.Equal(new[] { "Bachata" }, parsed.Terms);
    }

    [Fact]
    public void TermsLabelCommasWordCapAndFold()
    {
        var parsed = GlossaryPrompt.Parse(L(
            " Terms: Alpha Beta Gamma Delta Epsilon, Alpha Beta Gamma Delta Epsilon Zeta",
            "Terms: Cursor", "Gödde", "godde", "  "));
        Assert.Equal(new[] { "Alpha Beta Gamma Delta Epsilon", "Cursor", "Gödde" }, parsed.Terms);
        Assert.Equal(GlossaryPrompt.Fold("Gödde"), GlossaryPrompt.Fold("Godde"));
        Assert.NotEqual(GlossaryPrompt.Fold("Gödde"), GlossaryPrompt.Fold("Goedde"));
        Assert.Equal("i", GlossaryPrompt.Fold("I"));
        Assert.Equal("i", GlossaryPrompt.Fold("İ"));
    }

    [Fact]
    public void BothAnnotationLanguagesAndMultipleForms()
    {
        Assert.Equal(new[] { "Goedde", "Gödde" }, GlossaryPrompt.RejectedSpellings("Godde (not \"Goedde, Gödde\")"));
        Assert.Equal(new[] { "Cloud" }, GlossaryPrompt.RejectedSpellings("Claude (nicht \"Cloud\")"));
        Assert.Empty(GlossaryPrompt.RejectedSpellings("Claude"));
    }

    [Fact]
    public void HintIsEmptyForEmptyGlossary()
    {
        Assert.Equal("", GlossaryPrompt.Hint(L()));
        Assert.Equal("", GlossaryPrompt.Hint(L("  ", "")));
    }

    [Fact]
    public void HintHasReferenceBlockAndTieBreakers()
    {
        var hint = GlossaryPrompt.Hint(L("Sabaki", "Claude CLI (not \"Cloud CLI\")"));
        Assert.StartsWith("Reference vocabulary — correct spellings of names and terms this speaker uses.", hint);
        Assert.Contains("unintelligible, return an empty response.\nSabaki, Claude CLI\n\nHomophone tie-breakers — this refines", hint);
        Assert.EndsWith("never output either form unless you actually heard it.\n- \"Claude CLI\" — not \"Cloud CLI\"", hint);
        // The rejected spelling appears only in the tie-breaker, never in the vocabulary line.
        Assert.DoesNotContain("Sabaki, Claude CLI, Cloud CLI", hint);
    }

    [Fact]
    public void HintWithOnlyCorrectionsStartsWithTieBreakers()
    {
        var hint = GlossaryPrompt.Hint(L("Phrase one two three four five six (not \"x\")"));
        Assert.Equal("", GlossaryPrompt.Hint(L()));
        // Over the word cap: no tie-breaker and no term, so nothing to say.
        Assert.Equal("", hint);
        var multi = GlossaryPrompt.Hint(L("Bachata (not \"Batchata\", \"Bacchata\")"));
        Assert.Contains("- \"Bachata\" — not \"Batchata\" / \"Bacchata\"", multi);
    }

    [Fact]
    public void EchoTermsAreTheParsedTerms()
    {
        Assert.Equal(new[] { "Bachata" }, GlossaryPrompt.EchoTerms(L("Bachata (not \"Batchata\")", "Batchata")));
    }

    private static string Corrected(string text) => GlossaryPrompt.ReplacingRejectedSpelling("Cloud CLI", "Claude CLI", text);

    [Fact]
    public void CorrectsAfterLowercaseWordStartAndPunctuation()
    {
        Assert.Equal("Wie kann man am besten hier die Claude CLI verwenden?",
            Corrected("Wie kann man am besten hier die Cloud CLI verwenden?"));
        Assert.Equal("Claude CLI ist installiert.", Corrected("Cloud CLI ist installiert."));
        Assert.Equal("Erstens, Claude CLI einrichten.", Corrected("Erstens, Cloud CLI einrichten."));
    }

    [Fact]
    public void CapitalisedPredecessorProtectsCompound()
    {
        var text = "Ich nutze die Google Cloud CLI dafür.";
        Assert.Equal(text, Corrected(text));
        Assert.Equal("Claude CLI und Google Cloud CLI sind verschieden.",
            Corrected("Cloud CLI und Google Cloud CLI sind verschieden."));
        Assert.Equal("Claude CLI, nicht Claude CLI.", Corrected("Cloud CLI, nicht Cloud CLI."));
    }

    [Fact]
    public void WordBoundariesAndUmlautPredecessor()
    {
        var text = "Das Wort Cloud CLIs steht hier.";
        Assert.Equal(text, Corrected(text));
        Assert.Equal("Über Cloud CLI reden.", Corrected("Über Cloud CLI reden."));
        Assert.Equal("Reden wir über Claude CLI.", Corrected("Reden wir über Cloud CLI."));
    }

    [Fact]
    public void CorrectRejectedSpellingsUsesGlossaryAndSkipsBareWords()
    {
        var glossary = L("Claude CLI (not \"Cloud CLI\")", "Claude (nicht \"Cloud\")");
        Assert.Equal("die Claude CLI", GlossaryPrompt.CorrectRejectedSpellings("die Cloud CLI", glossary));
        Assert.Equal("Google Cloud CLI", GlossaryPrompt.CorrectRejectedSpellings("Google Cloud CLI", glossary));
        // Bare "Cloud" is never rewritten.
        var bare = "Meine Google Cloud Kosten und die Cloud allgemein.";
        Assert.Equal(bare, GlossaryPrompt.CorrectRejectedSpellings(bare, glossary));
        Assert.Equal("x", GlossaryPrompt.CorrectRejectedSpellings("x", L()));
        Assert.Equal("", GlossaryPrompt.CorrectRejectedSpellings("", glossary));
    }
}

public class TermSpellingTests
{
    [Theory]
    [InlineData("Herr Gödde kommt.", "goedde", "Herr Goedde kommt.")]
    [InlineData("herr gödde kommt.", "goedde", "herr goedde kommt.")]
    [InlineData("Herr Goedde kommt.", "goedde", "Herr Goedde kommt.")]
    [InlineData("Göttingen liegt in Niedersachsen.", "goedde", "Göttingen liegt in Niedersachsen.")]
    [InlineData("Gerade Gödde.", "Goedde", "Gerade Goedde.")]
    [InlineData("Görde", "goedde", "Görde")] // no fuzzy matching
    [InlineData("Herr Goedde", "Gödde", "Herr Gödde")] // the glossary spelling wins
    [InlineData("Die STRASSE ist gesperrt", "Straße", "Die Straße ist gesperrt")]
    [InlineData("Herr Müller und Herr Mueller", "Müller", "Herr Müller und Herr Müller")]
    [InlineData("Herr Gödde-Praxis", "goedde", "Herr Goedde-Praxis")]
    public void SingleWordTermIsSetToTheGlossarySpelling(string text, string term, string expected) =>
        Assert.Equal(expected, GlossaryPrompt.ApplyTermSpellings(text, new[] { term }));

    [Fact]
    public void MixedCaseTermKeepsItsOwnCasing() =>
        Assert.Equal("Ein EnBW Vertrag", GlossaryPrompt.ApplyTermSpellings("Ein enbw Vertrag", new[] { "EnBW" }));

    [Fact]
    public void MultiWordAndNonLetterTermsAreLeftAlone()
    {
        Assert.Equal("Magnus Gödde", GlossaryPrompt.ApplyTermSpellings("Magnus Gödde", new[] { "Magnus Goedde" }));
        Assert.Equal("sabaki dance", GlossaryPrompt.ApplyTermSpellings("sabaki dance", new[] { "sabaki.dance" }));
    }

    [Fact]
    public void EmptyInputsAreUnchanged()
    {
        Assert.Equal("", GlossaryPrompt.ApplyTermSpellings("", new[] { "goedde" }));
        Assert.Equal("Gödde", GlossaryPrompt.ApplyTermSpellings("Gödde", Array.Empty<string>()));
    }
}
