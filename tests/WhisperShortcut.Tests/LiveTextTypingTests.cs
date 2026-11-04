using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>Live diktieren into Chromium browsers: which programs get typing, and how a piece is cut into key events.</summary>
public class LiveTextTypingTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("brave")]
    [InlineData("vivaldi")]
    [InlineData("opera")]
    [InlineData("opera_gx")]
    [InlineData("Chrome")]
    [InlineData("MSEDGE")]
    public void Chromium_browsers_are_recognized(string name) => Assert.True(LiveTextTyping.IsChromium(name));

    [Theory]
    [InlineData("notepad")]
    [InlineData("firefox")]
    [InlineData("WINWORD")]
    [InlineData("chrome.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_programs_are_pasted_into(string? name) => Assert.False(LiveTextTyping.IsChromium(name));

    [Fact]
    public void Empty_text_has_no_chunks() => Assert.Empty(LiveTextTyping.Chunks(""));

    [Fact]
    public void Short_text_is_one_chunk()
    {
        var chunks = LiveTextTyping.Chunks("Hallo Welt");
        Assert.Equal(new[] { "Hallo Welt" }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Long_text_splits_at_20_units()
    {
        var chunks = LiveTextTyping.Chunks(new string('a', 45));
        Assert.Equal(new[] { 20, 20, 5 }, chunks.Select(c => c.Text!.Length));
    }

    [Fact]
    public void Surrogate_pairs_are_never_cut()
    {
        // 19 letters + one emoji (2 units) would be 21: the emoji starts the next chunk.
        var text = new string('a', 19) + "😀" + "b";
        var chunks = LiveTextTyping.Chunks(text);
        Assert.Equal(new[] { new string('a', 19), "😀b" }, chunks.Select(c => c.Text));
        Assert.All(chunks, c => Assert.True(c.Text!.Length <= 20));
    }

    [Fact]
    public void Grapheme_clusters_stay_whole()
    {
        var family = "👨‍👩‍👧"; // 8 units
        var chunks = LiveTextTyping.Chunks(new string('a', 15) + family);
        Assert.Equal(new[] { new string('a', 15), family }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Each_line_break_is_one_newline()
    {
        var chunks = LiveTextTyping.Chunks("a\nb\rc\r\nd");
        Assert.Equal(new[] { "a", null, "b", null, "c", null, "d" }, chunks.Select(c => c.Text));
        Assert.Equal(3, chunks.Count(c => c.IsNewline));
    }

    [Fact]
    public void Crlf_is_a_single_newline()
    {
        var chunks = LiveTextTyping.Chunks("\r\n");
        Assert.Single(chunks);
        Assert.True(chunks[0].IsNewline);
    }
}
