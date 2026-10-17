using WhisperShortcut.Core;
using Xunit;

namespace WhisperShortcut.Tests;

public class BareEditCommandTests
{
    [Theory]
    [InlineData("Korrigiere.")]
    [InlineData("Ähm, korrigiere")]
    [InlineData("Schreib das um!")]
    [InlineData("Mach das freundlicher.")]
    [InlineData("mach es etwas höflicher")]
    [InlineData("Rephrase this")]
    public void BareCommandsMatch(string instruction) => Assert.True(BareEditCommand.Matches(instruction));

    [Theory]
    [InlineData("Korrigiere, mein Geburtsdatum ist 15.08.91")]
    [InlineData("Schreib Herrn Meier ein Angebot für zwei Heizkörper.")]
    [InlineData("ähmlich")]
    [InlineData("")]
    [InlineData(null)]
    public void RealRequestsDoNotMatch(string? instruction) => Assert.False(BareEditCommand.Matches(instruction));
}
