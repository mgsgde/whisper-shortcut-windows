using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ReplyGuardsTests
{
    [Fact]
    public void LongSelectionMuchShorterAnswer_IsSuspicious() =>
        Assert.True(ReplyGuards.AnswerSuspiciouslyShort(32_000, 12_000, "Klarer formulieren"));

    [Fact]
    public void ExactlyFortyPercent_IsNotSuspicious() =>
        Assert.False(ReplyGuards.AnswerSuspiciouslyShort(10_000, 4_000, null));

    [Fact]
    public void ShortSelection_NeverSuspicious() =>
        Assert.False(ReplyGuards.AnswerSuspiciouslyShort(2_000, 10, null));

    [Theory]
    [InlineData("Kürzer fassen")]
    [InlineData("In Stichpunkten zusammenfassen")]
    [InlineData("Please SUMMARIZE this")]
    [InlineData("make it short")]
    [InlineData("kürze das")]
    public void ShorteningInstruction_IsNotSuspicious(string instruction) =>
        Assert.False(ReplyGuards.AnswerSuspiciouslyShort(30_000, 500, instruction));

    [Fact]
    public void UnknownInstruction_CountsAsNotShortening() =>
        Assert.True(ReplyGuards.AnswerSuspiciouslyShort(30_000, 500, null));

    [Fact]
    public void OwnWindow_MatchesOnlyOwnProcess()
    {
        Assert.True(ReplyGuards.IsOwnWindow(42, 42));
        Assert.False(ReplyGuards.IsOwnWindow(43, 42));
        Assert.False(ReplyGuards.IsOwnWindow(0, 0));
    }
}
