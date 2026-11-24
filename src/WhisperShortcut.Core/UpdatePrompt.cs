namespace WhisperShortcut.Core;

/// <summary>When the update window opens by itself (Mac Sparkle: once per found version, "Später" is respected).</summary>
public static class UpdatePrompt
{
    /// <summary>
    /// An automatic find opens the window once per version and run: not again for the version the user
    /// already saw (and closed with „Später“). A manual check always opens it.
    /// </summary>
    public static bool ShouldOpen(string version, string? lastShown, bool manual)
        => manual || !string.Equals(version, lastShown, StringComparison.Ordinal);
}
