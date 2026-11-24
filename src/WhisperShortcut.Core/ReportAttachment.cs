using System.Globalization;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>
/// The folder where earlier versions wrote the report as a .txt file (Mac ReportAttachment). The report now travels as
/// text in the mail; the app only clears what older versions left behind, since a report can quote a dictation.
/// </summary>
public static class ReportAttachment
{
    // Mac keeps report files 7 days. A report can quote a dictation, and Windows keeps dictations nowhere on disk
    // (practices): one day is enough to send it, and the app prunes at every start too.
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    /// <summary>The default folder of the report files.</summary>
    public static string DefaultDirectory => Path.Combine(AppData.LocalFolder, "Reports");

    /// <summary>Deletes report files last written more than a day before <paramref name="now"/>. A missing folder is nothing.</summary>
    public static void PruneOld(DateTime now, string? directory = null)
    {
        var folder = directory ?? DefaultDirectory;
        if (!Directory.Exists(folder)) return;
        var cutoff = now - Retention;
        string[] files;
        try { files = Directory.GetFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"DICTATION-REPORT: Could not prune {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }
}
