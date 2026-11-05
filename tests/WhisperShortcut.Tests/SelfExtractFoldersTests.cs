using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SelfExtractFoldersTests
{
    private static readonly string Temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wsw-se"));

    [Fact]
    public void FindsHashFolderOfALoadedModule()
    {
        var dll = Path.Combine(Temp, ".net", "WhisperShortcut", "abc123", "onnxruntime.dll");
        var found = SelfExtractFolders.Locate(dll, "WhisperShortcut");
        Assert.NotNull(found);
        Assert.Equal(Path.Combine(Temp, ".net", "WhisperShortcut"), found!.Value.Root);
        Assert.Equal(Path.Combine(Temp, ".net", "WhisperShortcut", "abc123"), found.Value.InUse);
    }

    [Theory]
    [InlineData("WhisperShortcut-Windows-x64")]
    [InlineData("whispershortcut")]
    public void AppNameMustMatchTheExe(string app)
    {
        var dll = Path.Combine(Temp, ".net", app, "h", "x.dll");
        Assert.NotNull(SelfExtractFolders.Locate(dll, app));
    }

    [Fact]
    public void OtherModulesAreNotInTheExtractFolder()
    {
        Assert.Null(SelfExtractFolders.Locate(Path.Combine(Temp, "system32", "kernel32.dll"), "WhisperShortcut"));
        Assert.Null(SelfExtractFolders.Locate(Path.Combine(Temp, ".net", "Other", "h", "x.dll"), "WhisperShortcut"));
        // Not inside a hash folder: the file would be the hash.
        Assert.Null(SelfExtractFolders.Locate(Path.Combine(Temp, ".net", "WhisperShortcut", "x.dll"), "WhisperShortcut"));
        Assert.Null(SelfExtractFolders.Locate(null, "WhisperShortcut"));
    }

    [Fact]
    public void StaleSkipsTheOneInUse()
    {
        var root = Path.Combine(Temp, ".net", "WhisperShortcut");
        var all = new[] { Path.Combine(root, "a"), Path.Combine(root, "B"), Path.Combine(root, "c") };
        var stale = SelfExtractFolders.Stale(root, Path.Combine(root, "b"), all).ToList();
        Assert.Equal(2, stale.Count);
        Assert.DoesNotContain(stale, s => s.EndsWith("B"));
    }
}
