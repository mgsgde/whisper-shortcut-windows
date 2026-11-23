using WhisperShortcut.Core;

namespace WhisperShortcut.Tests;

public class ReportAttachmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ws-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Write_names_the_file_and_writes_bom_and_crlf()
    {
        var now = new DateTime(2026, 11, 4, 9, 5, 7);
        var path = ReportAttachment.Write("Grüße\nzwei\r\ndrei", now, _dir);
        Assert.Equal("WhisperShortcut-Report-2026-11-04-090507.txt", Path.GetFileName(path));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Grüße\r\nzwei\r\ndrei", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

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
    public void Write_prunes_first_and_PruneOld_ignores_a_missing_folder()
    {
        ReportAttachment.PruneOld(DateTime.Now, Path.Combine(_dir, "nope"));
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "old.txt");
        File.WriteAllText(old, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        ReportAttachment.Write("a", DateTime.Now, _dir);
        Assert.False(File.Exists(old));
    }
}
