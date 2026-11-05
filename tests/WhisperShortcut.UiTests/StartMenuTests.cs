namespace WhisperShortcut.UiTests;

/// <summary>The Start menu shortcut (Windows search finds the app), written into a temp folder, never the real Start menu.</summary>
public sealed class StartMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsw-startmenu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesShortcut_AndFollowsAMovedExe()
    {
        Sta.Run(() =>
        {
            var lnk = Path.Combine(_dir, "Programs", "WhisperShortcut.lnk");
            var first = Path.Combine(_dir, "a", "WhisperShortcut.exe");
            var moved = Path.Combine(_dir, "b", "WhisperShortcut.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.WriteAllText(first, "");
            File.WriteAllText(moved, "");

            Assert.True(StartMenu.Write(lnk, first));
            Assert.True(File.Exists(lnk));
            Assert.Equal(first, StartMenu.ReadTarget(lnk), ignoreCase: true);
            Assert.False(StartMenu.Write(lnk, first)); // unchanged: not rewritten on every start

            Assert.True(StartMenu.Write(lnk, moved));
            Assert.Equal(moved, StartMenu.ReadTarget(lnk), ignoreCase: true);
        });
    }
}
