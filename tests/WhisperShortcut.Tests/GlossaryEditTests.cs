using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class GlossaryEditTests
{
    [Fact]
    public void Added_AppendsTheCleanedSpelling()
    {
        var glossary = new List<string> { "Alpha" };
        var result = GlossaryEdit.TryAdd(glossary, "  Gödde   und\tSöhne. ", out var term);
        Assert.Equal(GlossaryAddResult.Added, result);
        Assert.Equal("Gödde und Söhne", term);
        Assert.Equal(new[] { "Alpha", "Gödde und Söhne" }, glossary);
    }

    [Fact]
    public void QuotedName_DropsSurroundingQuotesAndOneTrailingMark()
    {
        var glossary = new List<string>();
        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, "„Gödde“.", out var term));
        Assert.Equal("Gödde", term);

        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, "‚Müller‘,", out term));
        Assert.Equal("Müller", term);
        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, "«Söhne»;", out term));
        Assert.Equal("Söhne", term);
        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, "\"Bachata\":", out term));
        Assert.Equal("Bachata", term);
        Assert.Equal(new[] { "Gödde", "Müller", "Söhne", "Bachata" }, glossary);
    }

    [Fact]
    public void Duplicate_MatchesCaseInsensitively_IncludingTheNameBeforeANote()
    {
        var glossary = new List<string> { "Gödde", "Bachata (not \"Batchata\")" };
        var result = GlossaryEdit.TryAdd(glossary, "bachata", out var term);
        Assert.Equal(GlossaryAddResult.Duplicate, result);
        Assert.Equal("bachata", term);

        Assert.Equal(GlossaryAddResult.Duplicate, GlossaryEdit.TryAdd(glossary, "GÖDDE", out term));
        Assert.Equal("GÖDDE", term);
        Assert.Equal(new[] { "Gödde", "Bachata (not \"Batchata\")" }, glossary);
    }

    [Fact]
    public void Empty_WhenNothingUsableRemains()
    {
        var glossary = new List<string> { "Keep" };
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, null, out var term));
        Assert.Equal("", term);
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, "   ", out term));
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, "\n", out term));
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, "\"\"", out term));
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, ".", out term));
        Assert.Equal(GlossaryAddResult.Empty, GlossaryEdit.TryAdd(glossary, "„“", out term));
        Assert.Equal(new[] { "Keep" }, glossary);
    }

    [Fact]
    public void TooLong_IsRejected_LengthIsMeasuredAfterCleanup()
    {
        var glossary = new List<string>();
        var tooLong = new string('a', GlossaryEdit.MaxTermLength + 1);
        Assert.Equal(GlossaryAddResult.TooLong, GlossaryEdit.TryAdd(glossary, tooLong, out var term));
        Assert.Equal(tooLong, term);
        Assert.Empty(glossary);

        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, new string('b', GlossaryEdit.MaxTermLength), out term));
        Assert.Equal(GlossaryEdit.MaxTermLength, term.Length);

        // Spaces collapse before the limit is applied, so this is one short term, not 100 characters.
        var collapsed = GlossaryEdit.TryAdd(glossary, "a" + new string(' ', 100) + "b", out term);
        Assert.Equal(GlossaryAddResult.Added, collapsed);
        Assert.Equal("a b", term);
    }

    [Fact]
    public void MultiLine_IsRejected()
    {
        var glossary = new List<string> { "Keep" };
        var result = GlossaryEdit.TryAdd(glossary, "foo\nbar", out var term);
        Assert.Equal(GlossaryAddResult.MultiLine, result);
        Assert.Equal("foo\nbar", term);
        Assert.Equal(GlossaryAddResult.MultiLine, GlossaryEdit.TryAdd(glossary, "\"foo\r\nbar\"", out term));
        Assert.Equal("foo\r\nbar", term);
        Assert.Equal(new[] { "Keep" }, glossary);

        // A trailing line break is just the clipboard's end of the line, not a second term.
        Assert.Equal(GlossaryAddResult.Added, GlossaryEdit.TryAdd(glossary, "foo\n", out term));
        Assert.Equal("foo", term);
    }
}
