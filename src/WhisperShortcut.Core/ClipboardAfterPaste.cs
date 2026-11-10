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
