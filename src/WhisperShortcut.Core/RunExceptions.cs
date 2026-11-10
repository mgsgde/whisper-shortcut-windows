namespace WhisperShortcut.Core;

public enum PasteOutcome
{
    /// <summary>Ctrl+V was sent; the old clipboard comes back after 3 s.</summary>
    Pasted,
    /// <summary>The target runs as administrator (UIPI would drop our keystrokes): text stays on the clipboard.</summary>
    ClipboardOnlyElevatedTarget,
    /// <summary>Ctrl/Alt were still held after 3 s: a Ctrl+V now would arrive as Ctrl+Alt+V. Text stays on the clipboard.</summary>
    ClipboardOnlyKeysHeld,
    /// <summary>"Automatisch einfügen" is off: the text was only put on the clipboard.</summary>
    ClipboardOnlyByChoice,
    /// <summary>The user switched to another window since the recording started and the old one could not be brought back. Text stays on the clipboard.</summary>
    ClipboardOnlyTargetChanged,
    /// <summary>The window the recording started in was closed. Text stays on the clipboard.</summary>
    ClipboardOnlyTargetGone,
    /// <summary>The window in front is one of our own (settings, a message box): Ctrl+V would vanish there. Text stays on the clipboard.</summary>
    ClipboardOnlyOwnWindow,
    /// <summary>Something else wrote the clipboard between our write and Ctrl+V: nothing pasted, the clipboard is left as it is.</summary>
    ClipboardOnlyClipboardChanged,
    /// <summary>Could not write the clipboard at all.</summary>
    Failed,
}

/// <summary>The clipboard changed after Ctrl+C but its text could not be read: there may be a selection we never saw.</summary>
public sealed class SelectionUnreadableException : Exception
{
    public SelectionUnreadableException() : base("Selection copied but the clipboard could not be read") { }
}

/// <summary>An offline model was needed but has not been downloaded yet.</summary>
public sealed class OfflineNotReadyException(string message) : Exception(message);
