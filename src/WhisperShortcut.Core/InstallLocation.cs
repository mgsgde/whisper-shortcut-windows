namespace WhisperShortcut.Core;

/// <summary>Where the running exe sits, as far as autostart, Start menu, updates and uninstall care.</summary>
public enum InstallPlace
{
    /// <summary>%LOCALAPPDATA%\Programs\WhisperShortcut: the one place the app is meant to live.</summary>
    Canonical,
    /// <summary>Under %TEMP%, e.g. an exe opened inside a ZIP in Explorer (Temp1_*.zip\). Vanishes.</summary>
    Temporary,
    Downloads,
    Desktop,
    Other,
}

/// <summary>
/// Pure path classification (no filesystem), so the "may register" rule and the move offer are testable.
/// </summary>
public static class InstallLocation
{
    /// <summary>The canonical folder below the given %LOCALAPPDATA%.</summary>
    public static string CanonicalDir(string localAppData) => Path.Combine(localAppData, "Programs", "WhisperShortcut");

    /// <summary>Order matters: Canonical wins, then Temporary (a ZIP opened from Downloads still runs from Temp).</summary>
    public static InstallPlace Classify(string exePath, string? tempDir, string? downloadsDir, string? desktopDir, string? canonicalDir)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return InstallPlace.Other;
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? ""; }
        catch (Exception) { return InstallPlace.Other; }
        if (SameDir(dir, canonicalDir)) return InstallPlace.Canonical;
        if (IsUnder(dir, tempDir)) return InstallPlace.Temporary;
        if (IsUnder(dir, downloadsDir)) return InstallPlace.Downloads;
        if (IsUnder(dir, desktopDir)) return InstallPlace.Desktop;
        return InstallPlace.Other;
    }

    /// <summary>
    /// The single rule for writing anything that points at this exe (Run value, Start menu, Uninstall entry):
    /// not from a debug build (it would send the user's real entries to a bin folder), not from a test run, and
    /// not from a temporary place that disappears.
    /// </summary>
    public static bool MayRegister(InstallPlace place, bool debugBuild, bool testRun) =>
        !debugBuild && !testRun && place != InstallPlace.Temporary;

    /// <summary>Places where the app offers to copy itself to the canonical folder.</summary>
    public static bool ShouldOfferMove(InstallPlace place) =>
        place is InstallPlace.Temporary or InstallPlace.Downloads or InstallPlace.Desktop;

    private static string Normalize(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SameDir(string dir, string? other)
    {
        if (string.IsNullOrWhiteSpace(other)) return false;
        try { return string.Equals(Normalize(dir), Normalize(other), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private static bool IsUnder(string dir, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var d = Normalize(dir);
            var r = Normalize(root);
            return d.Equals(r, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(r + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
