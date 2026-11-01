using System.IO.Compression;
using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class SafePathTests
{
    [Fact]
    public void CombineUnder_AllowsNestedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = SafePath.CombineUnder(dir, Path.Combine("sub", "file.bin"));
        Assert.True(SafePath.IsUnder(dir, dest));
        Assert.Equal("file.bin", Path.GetFileName(dest));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("..\\evil.bin")]
    [InlineData("/tmp/evil.bin")]
    public void CombineUnder_RejectsEscape(string relative)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => SafePath.CombineUnder(dir, relative));
    }

    [Fact]
    public void ExtractZipTo_RejectsZipSlip()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "out");
        Directory.CreateDirectory(dest);
        var zipPath = Path.Combine(root, "slip.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("../escape.exe").Open();
            s.Write("evil"u8);
        }
        Assert.Throws<ArgumentException>(() => SafePath.ExtractZipTo(zipPath, dest));
        Assert.False(File.Exists(Path.Combine(root, "escape.exe")));
        Directory.Delete(root, true);
    }
}
