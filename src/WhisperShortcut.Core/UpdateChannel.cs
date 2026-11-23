namespace WhisperShortcut.Core;

/// <summary>
/// Which releases the update check follows (Mac <c>UpdateChannel</c>). Every release is published as a GitHub prerelease
/// (Latest) and promoted about a week later (Stable). Stored as "stable" / "latest"; missing or unknown = Stable.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Only promoted releases (GitHub <c>releases/latest</c>). The default.</summary>
    Stable,

    /// <summary>Every release on the day it appears, prereleases included.</summary>
    Latest,
}
