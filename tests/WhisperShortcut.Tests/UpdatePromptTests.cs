using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdatePromptTests
{
    [Fact]
    public void New_version_opens() => Assert.True(UpdatePrompt.ShouldOpen("0.7.15", null, manual: false));

    [Fact]
    public void Same_version_after_Spaeter_does_not_reopen() => Assert.False(UpdatePrompt.ShouldOpen("0.7.15", "0.7.15", manual: false));

    [Fact]
    public void Newer_version_opens_again() => Assert.True(UpdatePrompt.ShouldOpen("0.7.16", "0.7.15", manual: false));

    [Fact]
    public void Manual_check_always_opens() => Assert.True(UpdatePrompt.ShouldOpen("0.7.15", "0.7.15", manual: true));
}
