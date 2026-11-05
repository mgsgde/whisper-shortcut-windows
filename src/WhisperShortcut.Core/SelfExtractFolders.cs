namespace WhisperShortcut.Core;

/// <summary>
/// The single-file exe extracts its native DLLs to %TEMP%\.net\&lt;exe name&gt;\&lt;hash&gt;\ (one hash per build), so every
/// update leaves another copy. This finds the folder in use from a loaded module's path; pure, so it is testable.
/// </summary>
public static class SelfExtractFolders
{
    /// <summary>
    /// If <paramref name="modulePath"/> lies in <c>...\.net\&lt;appName&gt;\&lt;hash&gt;\...</c>, returns (the &lt;appName&gt; folder,
    /// the &lt;hash&gt; folder); otherwise null. <paramref name="appName"/> = the exe's file name without ".exe".
    /// </summary>
    public static (string Root, string InUse)? Locate(string? modulePath, string appName)
    {
        if (string.IsNullOrWhiteSpace(modulePath) || string.IsNullOrWhiteSpace(appName)) return null;
        string full;
        try { full = Path.GetFullPath(modulePath); }
        catch (Exception) { return null; }
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 1; i + 3 < parts.Length; i++)
        {
            if (!parts[i].Equals(".net", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[i + 1].Equals(appName, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[i + 2].Length == 0) continue;
            // parts[i + 3] is a file name or deeper folder, so parts[i + 2] is the hash folder.
            var root = string.Join(Path.DirectorySeparatorChar, parts.Take(i + 2));
            var inUse = string.Join(Path.DirectorySeparatorChar, parts.Take(i + 3));
            return (root, inUse);
        }
        return null;
    }

    /// <summary>The hash folders under <paramref name="root"/> other than <paramref name="inUse"/>.</summary>
    public static IEnumerable<string> Stale(string root, string inUse, IEnumerable<string> folders) =>
        folders.Where(f => !string.Equals(Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(inUse).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
}
