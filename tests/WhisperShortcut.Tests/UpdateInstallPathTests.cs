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
    [InlineData("WhisperShortcut-Windows-x64.exe")]
    [InlineData("WhisperShortcut-Windows-arm64.exe")]
    [InlineData("WhisperShortcut (1).exe")]
    [InlineData("whispershortcut_2.EXE")]
    public void AcceptsDownloadNames(string name) =>
        Assert.True(UpdateInstallPath.IsAllowedTarget(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData("OtherWhisperShortcut.exe")]
    [InlineData("WhisperShortcut.exe.txt")]
    [InlineData("WhisperShortcut&calc.exe")]
    [InlineData("WhisperShortcut.dll")]
    public void RejectsOtherNames(string name) =>
        Assert.False(UpdateInstallPath.IsAllowedTarget(Path.Combine(Path.GetTempPath(), name)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("//server/share/WhisperShortcut.exe")]
    [InlineData("C:\\Apps\\WhisperShortcut.exe\" --install-update \"C:\\evil.exe")]
    public void RejectsOtherTargets(string? path) =>
        Assert.False(UpdateInstallPath.IsAllowedTarget(path));
}
