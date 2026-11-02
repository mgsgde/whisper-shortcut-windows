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
}
