using WhisperShortcut.Core.Mcp;

namespace WhisperShortcut.Tests.Mcp;

public class HotkeyRulesTests
{
    [Theory]
    [InlineData("Ctrl+Shift+D", "Ctrl+Shift+D")]
    [InlineData("strg+umschalt+d", "Ctrl+Shift+D")]
    [InlineData("Shift+Control+d", "Ctrl+Shift+D")]
    [InlineData("Ctrl+Alt+Shift+F5", "Ctrl+Alt+Shift+F5")]
    [InlineData("win+shift+ctrl+7", "Ctrl+Shift+Win+7")]
    [InlineData(" ctrl + shift + f12 ", "Ctrl+Shift+F12")]
    public void TryNormalize_Accepts(string input, string expected)
    {
        Assert.True(HotkeyRules.TryNormalize(input, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Foo+D")]
    [InlineData("Ctrl+Shift")]
    public void TryNormalize_Rejects(string? input)
    {
        Assert.False(HotkeyRules.TryNormalize(input, out var n));
        Assert.Equal("", n);
    }

    [Theory]
    [InlineData("Ctrl+Shift+D", true)]
    [InlineData("Alt+Shift+D", true)]
    [InlineData("Ctrl+Alt+Shift+D", true)]
    [InlineData("Ctrl+Shift+Win+D", true)]
    [InlineData("Ctrl+D", false)]
    [InlineData("Shift+D", false)]
    [InlineData("Ctrl+Alt+Q", false)]
    [InlineData("Win+Shift+D", false)]
    [InlineData("Ctrl+Win+D", false)]
    public void IsSafe_FollowsTheSettingsFormRule(string normalized, bool safe) =>
        Assert.Equal(safe, HotkeyRules.IsSafe(normalized));

    [Fact]
    public void Display_IsGerman() => Assert.Equal("Strg+Umschalt+D", HotkeyRules.Display("Ctrl+Shift+D"));
}
