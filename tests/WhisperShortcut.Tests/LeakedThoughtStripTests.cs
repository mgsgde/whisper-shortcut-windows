using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

/// <summary>
/// Gemini sometimes writes its reasoning into the visible answer between start_thought and
/// end_thought. Port of LeakedThoughtStripTests (Mac #443).
/// </summary>
public class LeakedThoughtStripTests
{
    [Fact]
    public void TextWithoutMarkersIsUnchangedApartFromTheLeadingWhitespaceTrim()
    {
        Assert.Equal("Hello there", TextCleanup.StripLeakedThought("Hello there"));
        Assert.Equal("Hello\nthere", TextCleanup.StripLeakedThought("Hello\nthere"));
        Assert.Equal("Hello", TextCleanup.StripLeakedThought("  \nHello"));
    }

    [Fact]
    public void AFullBlockAtTheStartIsRemovedAndTheAnswerIsKept()
    {
        var leaked = """
            start_thought
            Context: Work Chat (Teams) with colleague Mark.
            Wait, let's keep it close to dictation.
            end_thought
            Thanks Mark, I'll send the notes.
            """;
        Assert.Equal("Thanks Mark, I'll send the notes.", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AnEndThoughtWithNoOpenerDropsThePrefix()
    {
        var leaked = """
            Context: Work Chat with colleague Mark.
            Wait, let's keep it close to dictation.
            end_thought
            Thanks Mark, I'll send the notes.
            """;
        Assert.Equal("Thanks Mark, I'll send the notes.", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AStartThoughtWithNoCloserDropsTheTail()
    {
        var leaked = "Thanks Mark, I'll send the notes.\nstart_thought\nStill checking the spelling.";
        Assert.Equal("Thanks Mark, I'll send the notes.\n", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void TwoBlocksAreBothRemoved()
    {
        var leaked = "start_thought first end_thought Hello start_thought second end_thought there";
        Assert.Equal("Hello  there", TextCleanup.StripLeakedThought(leaked));
    }

    [Fact]
    public void AnOrphanEndThoughtBeforeAnUnclosedStartThoughtIsStillRemoved()
    {
        Assert.Equal("B", TextCleanup.StripLeakedThought("A end_thought B start_thought C"));
    }

    [Fact]
    public void NormalizeReplyStripsALeakedBlockBeforeTheWhitespaceCleanup()
    {
        var leaked = "start_thought Check spelling... end_thought\nHallo Mark";
        Assert.Equal("Hallo Mark", TextCleanup.NormalizeReply(leaked));
    }
}
