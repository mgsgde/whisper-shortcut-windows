using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ReportAttachmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ws-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void PruneOld_deletes_only_files_older_than_one_day()
    {
        Directory.CreateDirectory(_dir);
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        var old = Path.Combine(_dir, "old.txt");
        var fresh = Path.Combine(_dir, "fresh.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTime(old, now.AddDays(-1).AddMinutes(-1));
        File.SetLastWriteTime(fresh, now.AddHours(-23));
        ReportAttachment.PruneOld(now, _dir);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void PruneOld_ignores_a_missing_folder()
    {
        ReportAttachment.PruneOld(DateTime.Now, Path.Combine(_dir, "nope"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "nope")));
    }
}
