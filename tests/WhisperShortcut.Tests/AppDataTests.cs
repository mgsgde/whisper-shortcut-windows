using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class AppDataTests
{
    [Fact]
    public void Folders_AreDistinctAndCoverSettingsAndLocal()
    {
        var folders = AppData.Folders;
        Assert.Equal(folders.Count, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(Path.GetFullPath(AppData.SettingsFolder), folders);
        Assert.Contains(Path.GetFullPath(AppData.LocalFolder), folders);
    }

    [Fact]
    public void Reset_DeletesFoldersRecursively_AndIgnoresMissingOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b", "Offline");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "sub", "settings.json"), "{}");
        File.WriteAllText(Path.Combine(b, "model.bin"), "x");

        AppData.Reset(new[] { a, b, Path.Combine(root, "missing") });

        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
        Directory.Delete(root, true);
        Assert.False(AppData.ResetDone); // only a reset of the real folders sets it
    }
}
