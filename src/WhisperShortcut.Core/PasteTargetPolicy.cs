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
