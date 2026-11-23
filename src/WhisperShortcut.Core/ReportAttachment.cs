using System.Globalization;
using System.Text;

namespace WhisperShortcut.Core;

/// <summary>The report written as a .txt file next to the app's other data, to attach to the mail (Mac ReportAttachment).</summary>
public static class ReportAttachment
{
    // Mac keeps report files 7 days. A report can quote a dictation, and Windows keeps dictations nowhere on disk
    // (practices): one day is enough to send it, and the app prunes at every start too.
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    /// <summary>The default folder of the report files.</summary>
    public static string DefaultDirectory => Path.Combine(AppData.LocalFolder, "Reports");

    /// <summary>
    /// Writes <paramref name="text"/> to WhisperShortcut-Report-yyyy-MM-dd-HHmmss.txt (UTF-8 with BOM, CRLF line endings),
    /// after pruning files older than 7 days. Returns the full path.
    /// </summary>
    public static string Write(string text, DateTime now, string? directory = null)
    {
        var folder = directory ?? DefaultDirectory;
        Directory.CreateDirectory(folder);
        PruneOld(now, folder);
        var name = "WhisperShortcut-Report-" + now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + ".txt";
        var path = Path.Combine(folder, name);
        var crlf = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        File.WriteAllText(path, crlf, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Info($"DICTATION-REPORT: Wrote {name} ({text.Length} chars)");
        return path;
    }

    /// <summary>Deletes report files last written more than 7 days before <paramref name="now"/>. A missing folder is nothing.</summary>
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
