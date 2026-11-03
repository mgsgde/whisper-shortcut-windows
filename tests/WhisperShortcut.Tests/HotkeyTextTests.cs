using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class HotkeyTextTests
{
    [Theory]
    [InlineData("Alt+1", HotkeyText.Alt, "1", "Alt+1", "Alt+1")]
    [InlineData("alt+0", HotkeyText.Alt, "0", "Alt+0", "Alt+0")]
    [InlineData("Ctrl+Shift+D", HotkeyText.Ctrl | HotkeyText.Shift, "D", "Ctrl+Shift+D", "Strg+Umschalt+D")]
    [InlineData("Shift+Strg+D", HotkeyText.Ctrl | HotkeyText.Shift, "D", "Ctrl+Shift+D", "Strg+Umschalt+D")]
    [InlineData("RightCtrl", 0u, HotkeyText.RightCtrl, "RightCtrl", "rechte Strg")]
    [InlineData("rightControl", 0u, HotkeyText.RightCtrl, "RightCtrl", "rechte Strg")]
    [InlineData("rechte Strg", 0u, HotkeyText.RightCtrl, "RightCtrl", "rechte Strg")]
    public void Parse_And_Format_RoundTrip(string text, uint mods, string key, string stored, string display)
    {
        Assert.True(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal(mods, m);
        Assert.Equal(key, k);
        Assert.Equal(stored, HotkeyText.Format(m, k, german: false));
        Assert.Equal(display, HotkeyText.Format(m, k, german: true));
        // The stored form parses back to the same value.
        Assert.True(HotkeyText.TryParse(stored, out var m2, out var k2));
        Assert.Equal((m, k), (m2, k2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("1")]
    [InlineData("Alt+")]
    [InlineData("Alt+1+2")]
    [InlineData("Ctrl+RightCtrl+X")]
    public void Parse_Rejects(string? text)
    {
        Assert.False(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal(0u, m);
        Assert.Equal("", k);
    }

    [Theory]
    [InlineData("Alt+1", true)]
    [InlineData("Alt+0", true)]
    [InlineData("Alt+9", true)]
    [InlineData("RightCtrl", true)]
    [InlineData("Ctrl+Shift+D", true)]
    [InlineData("Alt+Shift+D", true)]
    [InlineData("Ctrl+Alt+Shift+1", true)]
    [InlineData("Alt+D", false)]
    [InlineData("Alt+F4", false)]
    [InlineData("Ctrl+1", false)]
    [InlineData("Shift+1", false)]
    [InlineData("Win+1", false)]
    [InlineData("Alt+Win+1", false)]
    [InlineData("Ctrl+Alt+1", false)]
    [InlineData("Ctrl+Alt+Q", false)]
    public void IsSafe_AllowsAltDigitAndRightCtrl_RefusesAltGr(string text, bool safe)
    {
        Assert.True(HotkeyText.TryParse(text, out var m, out var k));
        Assert.Equal(safe, HotkeyText.IsSafe(m, k));
    }
}
