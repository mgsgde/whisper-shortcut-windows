using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class UpdateInstallPathTests
{
    [Fact]
    public void AcceptsLocalWhisperShortcutExe()
    {
        var path = Path.Combine(Path.GetTempPath(), "WhisperShortcut.exe");
        Assert.True(UpdateInstallPath.IsAllowedTarget(path));
        Assert.True(UpdateInstallPath.IsAllowedTarget(path.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("//server/share/WhisperShortcut.exe")]
    [InlineData("C:\\Apps\\WhisperShortcut.exe\" --install-update \"C:\\evil.exe")]
    public void RejectsOtherTargets(string? path) =>
        Assert.False(UpdateInstallPath.IsAllowedTarget(path));
}
