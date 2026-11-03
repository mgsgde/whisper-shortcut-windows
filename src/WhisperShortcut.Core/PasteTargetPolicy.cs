namespace WhisperShortcut.Core;

/// <summary>What is in front right now, as far as the paste decision cares.</summary>
public enum ForegroundKind
{
    /// <summary>No foreground window.</summary>
    None,
    /// <summary>A window of this process.</summary>
    Own,
    /// <summary>Taskbar, tray overflow or desktop.</summary>
    Shell,
    /// <summary>Any other top-level window (including another window of the target's own app).</summary>
    Foreign,
}

public enum PasteTargetAction
{
    PasteNow,
    ActivateTargetThenPaste,
    ClipboardOnlyOwnWindow,
    ClipboardOnlyTargetChanged,
    ClipboardOnlyTargetGone,
}

/// <summary>
/// Where a dictation may be pasted: only into the window the recording started in, and only when the
/// user has not moved on. Port of the Mac's PasteTargetPolicy, per window.
/// </summary>
public static class PasteTargetPolicy
{
    public static PasteTargetAction Decide(bool hasTarget, bool targetIsOwn, bool targetExists,
        bool targetMinimised, bool foregroundIsTarget, ForegroundKind foreground)
    {
        if (!hasTarget)
            return foreground == ForegroundKind.Own ? PasteTargetAction.ClipboardOnlyOwnWindow : PasteTargetAction.PasteNow;
        if (targetIsOwn) return PasteTargetAction.ClipboardOnlyOwnWindow;
        if (!targetExists) return PasteTargetAction.ClipboardOnlyTargetGone;
        if (foregroundIsTarget) return PasteTargetAction.PasteNow;
        // WHY: the user moved to another window on purpose; like the Mac, do not yank focus back and
        // never paste into a window the recording did not start in.
        if (foreground == ForegroundKind.Foreign) return PasteTargetAction.ClipboardOnlyTargetChanged;
        if (targetMinimised) return PasteTargetAction.ClipboardOnlyTargetChanged;
        // Nothing, our own window or the taskbar in front (e.g. after clicking the tray icon or the pill).
        return PasteTargetAction.ActivateTargetThenPaste;
    }

    /// <summary>Window classes of the taskbar, the tray overflow and the desktop.</summary>
    public static bool IsShellWindowClass(string? className) => className is
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
        or "TopLevelWindowForOverflowXamlIsland" or "Progman" or "WorkerW";
}

/// <summary>
/// Whether the clipboard still holds exactly what auto-paste put there. The synthetic Ctrl+V is read by
/// the receiving app some time later (lazily in browsers, Citrix, RDP), so a clipboard that changed in
/// between would paste the previous clipboard instead. Pure functions so they are testable without a clipboard.
/// </summary>
public static class ClipboardPasteGuard
{
    /// <summary>Pause between writing our text and Ctrl+V.</summary>
    public static readonly TimeSpan PrePasteDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Slow receivers (browser, Citrix, RDP) read the clipboard lazily; restoring earlier makes them paste the old content.</summary>
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(3.0);

    /// <summary>Ctrl+V may be sent only while the clipboard is the one we wrote.</summary>
    public static bool PasteAllowed(uint sequenceAtWrite, uint currentSequence, string? expectedText, string? currentText) =>
        sequenceAtWrite == currentSequence && string.Equals(currentText, expectedText, StringComparison.Ordinal);

    /// <summary>The old clipboard may be put back only if nothing wrote since our write.</summary>
    public static bool RestoreAllowed(uint sequenceAtWrite, uint currentSequence) => sequenceAtWrite == currentSequence;

    /// <summary>
    /// A run started from idle (retry from the tray menu or balloon, a recovered recording) has no recording
    /// start, so its result is copied only, never pasted into whatever window is in front.
    /// </summary>
    public static bool MayAutoPaste(bool autoPasteSetting, bool clipboardOnlyRun) => autoPasteSetting && !clipboardOnlyRun;
}
