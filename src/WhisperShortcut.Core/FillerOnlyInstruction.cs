namespace WhisperShortcut.Core;

/// <summary>
/// A spoken Dictate Prompt instruction that is only a filler ("Mhm.", "Äh, ähm."), a lone "0"
/// (what a transcriber writes for a breath), or <see cref="NoSpokenInstructionMarker"/>. Nothing
/// was asked, so the turn must not edit a selection or write text (Mac #510,
/// <c>DictatePromptSelectionDecision.isFillerOnlyInstruction</c>).
/// The hesitation words are <see cref="LiveDictation.FillerRegex"/> (Mac #507), not a second list.
/// "oh", "ah" and "0" are instruction-only: inside a transcript they stay, as whole words.
/// Empty input is not a filler. The timeout placeholder <see cref="PromptConversationMemory.SpokenPlaceholder"/>
/// is not a filler either. A quick action's text is typed, and a template turn formats its selection,
/// so <see cref="Refuses"/> does not judge those.
/// </summary>
public static class FillerOnlyInstruction
{
    /// <summary>History transcription heard nothing. A filler, so the turn is not pasted.</summary>
    public const string NoSpokenInstructionMarker = "(no spoken instruction)";

    private static readonly char[] Punctuation = { '.', ',', '!', '?', '\u2026' };
    private static readonly char[] Separators = { ' ', '\t', '\r', '\n', ',' };

    /// <summary>True when <paramref name="instruction"/> is only fillers, punctuation, a lone 0, or no speech.</summary>
    public static bool Is(string? instruction)
    {
        if (instruction == NoSpokenInstructionMarker) return true;
        var trimmed = (instruction ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return false;
        var stripped = trimmed.Trim(Punctuation).Trim();
        if (stripped.Length == 0) return true;

        // No real token left (only punctuation between the separators) is a filler.
        // One real word ("Korrigiere", "kürze") is not.
        foreach (var piece in stripped.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = piece.Trim(Punctuation).Trim();
            if (token.Length == 0) continue;
            if (!IsFillerToken(token)) return false;
        }
        return true;
    }

    /// <summary>
    /// A filler-only spoken instruction is refused. A quick action's text is typed, and a template
    /// turn formats its selection, so neither is judged here.
    /// </summary>
    public static bool Refuses(string? instruction, bool isQuickAction = false, bool isTemplateTurn = false) =>
        !isQuickAction && !isTemplateTurn && Is(instruction);

    private static bool IsFillerToken(string token)
    {
        // "Oh." and "Ah." as the whole instruction are breaths. "0" is the transcriber's breath.
        // They are not in the #507 list: that list keeps them inside a real sentence.
        if (token is "oh" or "ah" or "0") return true;
        var match = LiveDictation.FillerRegex.Match(token);
        return match.Success && match.Index == 0 && match.Length == token.Length;
    }
}
