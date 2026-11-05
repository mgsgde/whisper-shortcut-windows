namespace WhisperShortcut.Core;

/// <summary>What <c>--install-update</c> may overwrite: a local WhisperShortcut*.exe, never a UNC path.</summary>
public static class UpdateInstallPath
{
    /// <summary>The name inside the update ZIP and in the canonical install folder.</summary>
    public const string ExeName = "WhisperShortcut.exe";

    /// <summary>
    /// True when <paramref name="targetExe"/> resolves to a local file named
    /// <see cref="ExeName"/> or a download variant of it (WhisperShortcut-Windows-x64.exe, "WhisperShortcut (1).exe").
    /// WHY: releases publish the arch-specific names and browsers add " (1)"; users run the exe where it landed,
    /// and the updater must still replace that file. Existence is not required (the caller checks that at install time).
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
        return IsAllowedName(Path.GetFileName(full));
    }

    private static readonly System.Text.RegularExpressions.Regex NamePattern = new(
        @"^WhisperShortcut[-_ .()\w]*\.exe$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True for WhisperShortcut.exe and its download variants (case-insensitive).</summary>
    public static bool IsAllowedName(string? fileName) => fileName != null && NamePattern.IsMatch(fileName);
}
