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

    [Fact]
    public void Reset_ListsFoldersThatCannotBeDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        var busy = Path.Combine(locked, "busy.bin");
        File.WriteAllText(busy, "x");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var fs = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None);
                var ex = Assert.Throws<IOException>(() => AppData.Reset(new[] { locked }));
                Assert.Contains("Nicht gelöscht werden konnte", ex.Message);
                Assert.Contains(locked, ex.Message);
            }
            else
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    var ex = Assert.Throws<IOException>(() => AppData.Reset(new[] { locked }));
                    Assert.Contains("Nicht gelöscht werden konnte", ex.Message);
                }
                finally
                {
                    File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Assert.False(AppData.ResetDone);
    }
}
