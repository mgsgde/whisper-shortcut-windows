namespace WhisperShortcut.Core;

/// <summary>What <c>--install-update</c> may overwrite: a local WhisperShortcut.exe, never a UNC path.</summary>
public static class UpdateInstallPath
{
    /// <summary>The only file name the updater replaces.</summary>
    public const string ExeName = "WhisperShortcut.exe";

    /// <summary>
    /// True when <paramref name="targetExe"/> resolves to a local file named
    /// <see cref="ExeName"/>. Existence is not required (the caller checks that at install time).
    /// </summary>
    public static bool IsAllowedTarget(string? targetExe)
    {
        if (string.IsNullOrWhiteSpace(targetExe)) return false;
        if (targetExe.Contains('"') || targetExe.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        var trimmed = targetExe.Trim();
        // GetFullPath on Unix turns "//server/share/x" into "/server/share/x"; reject the raw form too.
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        string full;
        try { full = Path.GetFullPath(trimmed); }
        catch (Exception) { return false; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            return false;
        return string.Equals(Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase);
    }
}
