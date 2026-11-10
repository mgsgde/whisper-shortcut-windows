namespace WhisperShortcut.Core;

/// <summary>Why a recording is being transcribed; decides which backstops run on the text (see <see cref="TranscriptionPolicy"/>).</summary>
public enum TranscribePurpose
{
    /// <summary>Dictate: the transcript is what gets pasted.</summary>
    Dictation,
    /// <summary>Dictate Prompt with a text model that cannot hear, or the bare-command check: the transcript is an instruction, not text.</summary>
    Instruction,
    /// <summary>A dictation template: the transcript is the body the template formats.</summary>
    TemplateBody,
    /// <summary>A quick instruction (Ctrl+1–9): whatever was said is the text or addition.</summary>
    QuickActionTranscript,
    /// <summary>One live-meeting chunk: the transcript goes to the meeting log.</summary>
    MeetingChunk,
    /// <summary>One piece of Live diktieren, always decoded on this PC.</summary>
    LivePiece,
}

/// <summary>
/// What runs around one transcription, per purpose and engine. This table is how the callers behaved when it was
/// written down (R3), including the places where they disagree; it is deliberately NOT tidied.
/// </summary>
/// <param name="GlossaryBackstops">Rejected spellings are corrected and glossary names set to their spelling.</param>
/// <param name="EchoStrip">A glossary list tacked onto the end is cut off (only meaningful with <paramref name="GlossaryBackstops"/>).</param>
/// <param name="SendKeywords">The glossary goes to gpt-transcribe as keywords (Gemini ignores it; nothing is sent on this PC).</param>
/// <param name="SpeechGate">Silero VAD in front of the cloud upload (Mac: dictation only; this PC never uses it).</param>
/// <param name="PunctuationFromSettings">The Settings punctuation mode applies (then <see cref="PunctuationModes.Effective"/> keeps it on this PC only); else the model's own.</param>
public readonly record struct TranscriptionPolicy(bool GlossaryBackstops, bool EchoStrip, bool SendKeywords, bool SpeechGate, bool PunctuationFromSettings)
{
    /// <summary>The policy for <paramref name="purpose"/> with <paramref name="engine"/> doing the speech recognition.</summary>
    public static TranscriptionPolicy For(TranscribePurpose purpose, Engine engine)
    {
        bool local = engine == Engine.Local;
        return purpose switch
        {
            TranscribePurpose.Dictation => new(true, true, !local, !local, true),
            // Prompt from transcript and the bare-command check use the raw transcript: no backstops at all.
            TranscribePurpose.Instruction => new(false, false, !local, false, false),
            // Parakeet gets the glossary as hotwords but no prompt to echo, so its text skips the strip (spelling fixes still apply).
            TranscribePurpose.TemplateBody or TranscribePurpose.QuickActionTranscript => new(true, !local, !local, false, false),
            // Live-meeting chunks carry loud system audio that gpt-transcribe answers with the keywords themselves.
            TranscribePurpose.MeetingChunk => new(true, true, false, false, false),
            // Live diktieren decodes on this PC whatever the engine setting says.
            TranscribePurpose.LivePiece => new(true, true, false, false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };
    }

    /// <summary>The punctuation mode to transcribe with: the stored one where the policy takes it and <paramref name="engine"/> is on this PC, else automatic.</summary>
    public PunctuationMode Punctuation(PunctuationMode stored, Engine engine) =>
        PunctuationFromSettings ? PunctuationModes.Effective(stored, isOffline: engine == Engine.Local, duringMeeting: false) : PunctuationMode.Automatic;
}
