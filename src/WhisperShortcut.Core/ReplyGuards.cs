namespace WhisperShortcut.Core;

/// <summary>Decisions about whether a reply may be pasted straight over the user's text.</summary>
public static class ReplyGuards
{
    /// <summary>Selections up to this length are never flagged (a short answer to a short text is normal).</summary>
    public const int MinSelectionLength = 2000;

    /// <summary>An answer below this share of the selection looks like lost text.</summary>
    public const double MinAnswerShare = 0.4;

    private static readonly string[] ShorteningWords = { "kürz", "zusammenfass", "stichpunkt", "short", "summar" };

    /// <summary>
    /// True when an edit answer is much shorter than a long selection although the instruction did not
    /// ask for a shorter text: the model may have dropped part of it. <paramref name="instruction"/> is
    /// null when it is unknown (spoken), which counts as not shortening.
    /// </summary>
    public static bool AnswerSuspiciouslyShort(int selectionLength, int answerLength, string? instruction)
    {
        if (selectionLength <= MinSelectionLength) return false;
        if (answerLength >= selectionLength * MinAnswerShare) return false;
        return !AsksForShortening(instruction);
    }

    /// <summary>True when the instruction names a shortening (kürzen, zusammenfassen, Stichpunkte, short, summarize).</summary>
    public static bool AsksForShortening(string? instruction) =>
        !string.IsNullOrEmpty(instruction)
        && ShorteningWords.Any(w => instruction.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A paste must never go into one of our own windows (settings, a message box): the text would be
    /// lost there. <paramref name="windowProcessId"/> 0 (no window) is not ours.
    /// </summary>
    public static bool IsOwnWindow(uint windowProcessId, uint ownProcessId) =>
        windowProcessId != 0 && windowProcessId == ownProcessId;
}
