namespace WhisperShortcut.Core;

public enum CopyPollResult { Wait, Selection, NoSelection, Unreadable }

/// <summary>Decisions for the synthetic Ctrl+C that fetches the selection.</summary>
public static class SelectionCopyPolicy
{
    public static readonly TimeSpan CopyDeadline = TimeSpan.FromMilliseconds(2000);

    /// <summary>Once the clipboard changed, a slow app (Citrix, big Word documents) gets at least this long to make the text readable.</summary>
    public static readonly TimeSpan ReadGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The clipboard changed for this Ctrl+C: sequence moved AND the new sequence is not our own last
    /// write (a pending paste restore firing during the poll puts back the user's OLD clipboard,
    /// maybe the previous patient's note; the Mac skips its own writes the same way). lastOwnWrite 0 = none.
    /// </summary>
    public static bool CopyChanged(uint before, uint now, uint lastOwnWrite)
        => now != before && (lastOwnWrite == 0 || now != lastOwnWrite);

    public static CopyPollResult Poll(bool changed, bool textReadable, TimeSpan elapsed, TimeSpan? changedAt)
    {
        if (!changed) return elapsed >= CopyDeadline ? CopyPollResult.NoSelection : CopyPollResult.Wait;
        if (textReadable) return CopyPollResult.Selection;
        var give = (changedAt ?? elapsed) + ReadGrace;
        var limit = give > CopyDeadline ? give : CopyDeadline;
        return elapsed >= limit ? CopyPollResult.Unreadable : CopyPollResult.Wait;
    }

    static readonly System.Text.RegularExpressions.Regex EmptySelectionMarker =
        new(@"""isFromEmptySelection""\s*:\s*true",System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the copied clipboard content is a VS Code-family "copy with no selection" (Mac 5e041f0a, #383).
    /// In VS Code, Cursor and Windsurf, Ctrl+C with nothing selected copies the whole current line; that line
    /// is not a selection and must not reach the model as one. The editor marks such copies with
    /// <c>"isFromEmptySelection":true</c> in its <c>vscode-editor-data</c> metadata, exposed either as that
    /// clipboard format or inside Chromium's custom-data pickle (strings are UTF-16LE there; UTF-8 is a fallback).
    /// </summary>
    public static bool IsEditorEmptySelectionLineCopy(byte[]? customData, string? vscodeEditorData)
    {
        if (vscodeEditorData != null && EmptySelectionMarker.IsMatch(vscodeEditorData)) return true;
        if (customData == null) return false;
        return EmptySelectionMarker.IsMatch(System.Text.Encoding.Unicode.GetString(customData))
            || EmptySelectionMarker.IsMatch(System.Text.Encoding.UTF8.GetString(customData));
    }
}
