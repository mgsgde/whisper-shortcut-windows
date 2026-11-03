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
    [InlineData("alt+1", "Alt+1")]
    [InlineData("Alt+0", "Alt+0")]
    [InlineData("RightCtrl", "RightCtrl")]
    [InlineData("rightControl", "RightCtrl")]
    [InlineData("rechte Strg", "RightCtrl")]
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
    [InlineData("Alt+1", true)]
    [InlineData("Alt+0", true)]
    [InlineData("RightCtrl", true)]
    [InlineData("Alt+D", false)]
    [InlineData("Ctrl+1", false)]
    [InlineData("Ctrl+Alt+1", false)]
    [InlineData("Alt+Win+1", false)]
    public void IsSafe_FollowsTheSettingsFormRule(string normalized, bool safe) =>
        Assert.Equal(safe, HotkeyRules.IsSafe(normalized));

    [Fact]
    public void Display_IsGerman() => Assert.Equal("Strg+Umschalt+D", HotkeyRules.Display("Ctrl+Shift+D"));

    [Theory]
    [InlineData("Alt+1", "Alt+1")]
    [InlineData("RightCtrl", "rechte Strg")]
    public void Display_AltDigitAndRightCtrl(string normalized, string display) =>
        Assert.Equal(display, HotkeyRules.Display(normalized));

    [Fact]
    public void Catalog_AcceptsAltDigit_AndObjectForm_RejectsAltGr()
    {
        Assert.True(McpSettingsCatalog.TryHotkey(System.Text.Json.Nodes.JsonNode.Parse("{\"key\":\"1\",\"modifiers\":[\"alt\"]}"), out var n));
        Assert.Equal("Alt+1", n);
        Assert.True(McpSettingsCatalog.TryHotkey(System.Text.Json.Nodes.JsonNode.Parse("{\"key\":\"rightControl\",\"modifiers\":[]}"), out var r));
        Assert.Equal("RightCtrl", r);
        Assert.False(HotkeyRules.IsSafe("Ctrl+Alt+D"));
    }
}
