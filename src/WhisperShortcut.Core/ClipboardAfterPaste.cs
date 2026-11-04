namespace WhisperShortcut.Core;

/// <summary>
/// What happens to the clipboard after auto-paste (Mac: General → "After auto-paste", ClipboardBehaviorSection).
/// Without auto-paste the text simply stays on the clipboard.
/// </summary>
public enum ClipboardAfterPaste
{
    /// <summary>The dictated text stays on the clipboard (Mac "Leave dictation", the default for new installs).</summary>
    KeepDictation,

    /// <summary>The previous clipboard comes back after <see cref="ClipboardPasteGuard.RestoreDelay"/> (Mac "Restore previous").</summary>
    RestorePrevious,

    /// <summary>The clipboard is emptied after <see cref="ClipboardPasteGuard.RestoreDelay"/>, if it still holds our text (Mac "Empty it").</summary>
    Clear,
}

/// <summary>Names (JSON, MCP) and German labels of <see cref="ClipboardAfterPaste"/>.</summary>
public static class ClipboardAfterPasteModes
{
    /// <summary>All options in the order the settings window lists them (Mac order).</summary>
    public static readonly IReadOnlyList<ClipboardAfterPaste> All =
        new[] { ClipboardAfterPaste.KeepDictation, ClipboardAfterPaste.RestorePrevious, ClipboardAfterPaste.Clear };

    /// <summary>Stored name: "keep", "restore" or "clear".</summary>
    public static string Name(ClipboardAfterPaste mode) => mode switch
    {
        ClipboardAfterPaste.RestorePrevious => "restore",
        ClipboardAfterPaste.Clear => "clear",
        _ => "keep",
    };

    /// <summary>Parses a stored name; null for a missing or unknown one (the caller decides the fallback).</summary>
    public static ClipboardAfterPaste? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "keep" => ClipboardAfterPaste.KeepDictation,
        "restore" => ClipboardAfterPaste.RestorePrevious,
        "clear" => ClipboardAfterPaste.Clear,
        _ => null,
    };

    /// <summary>
    /// The mode of a settings file: its own field when it has one, otherwise the old switch "restoreClipboard"
    /// (true or missing = restore, which was the old default; false = keep the dictation).
    /// </summary>
    public static ClipboardAfterPaste FromFile(string? stored, bool? legacyRestoreClipboard) =>
        Parse(stored) ?? (legacyRestoreClipboard ?? true ? ClipboardAfterPaste.RestorePrevious : ClipboardAfterPaste.KeepDictation);

    /// <summary>Label in the settings window (German, from the Mac strings).</summary>
    public static string Label(ClipboardAfterPaste mode) => mode switch
    {
        ClipboardAfterPaste.RestorePrevious => "Vorherigen Inhalt zurücklegen",
        ClipboardAfterPaste.Clear => "Leeren",
        _ => "Diktat stehen lassen",
    };

    /// <summary>The explanation line under the choice (Mac wording, du-form, Windows keys).</summary>
    public static string Explanation(ClipboardAfterPaste mode) => mode switch
    {
        ClipboardAfterPaste.RestorePrevious =>
            "Legt 3 Sekunden nach dem Einfügen zurück, was du vorher kopiert hattest.",
        ClipboardAfterPaste.Clear =>
            "Leert die Zwischenablage 3 Sekunden nach dem Einfügen, damit ein späteres Strg+V kein früheres Diktat einfügt. "
            + "„Letzte Transkription kopieren“ im Menü am Symbol funktioniert weiterhin.",
        _ => "Der diktierte Text bleibt in der Zwischenablage, du kannst ihn mit Strg+V erneut einfügen.",
    };
}
