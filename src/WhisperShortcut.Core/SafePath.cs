using System.IO.Compression;

namespace WhisperShortcut.Core;

/// <summary>Keeps extracted and downloaded files inside a destination folder (zip-slip / <c>..</c>).</summary>
public static class SafePath
{
    /// <summary>
    /// <paramref name="directory"/> plus <paramref name="relative"/>, or throws
    /// <see cref="ArgumentException"/> when the result would leave the directory.
    /// </summary>
    public static string CombineUnder(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new ArgumentException("Empty relative path.", nameof(relative));
        if (Path.IsPathRooted(relative))
            throw new ArgumentException("Absolute paths are not allowed.", nameof(relative));
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
                throw new ArgumentException("Path navigation is not allowed.", nameof(relative));
        }
        var root = Path.GetFullPath(directory);
        var dest = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsUnder(root, dest))
            throw new ArgumentException("Path escapes the destination folder.", nameof(relative));
        return dest;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or a file under it.</summary>
    public static bool IsUnder(string root, string path)
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts every file of <paramref name="zipPath"/> into <paramref name="destDir"/>.
    /// Entries that would write outside the folder abort the extract.
    /// </summary>
    public static void ExtractZipTo(string zipPath, string destDir)
    {
        destDir = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory marker
            var dest = CombineUnder(destDir, entry.FullName);
            var parent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}
