namespace WhisperShortcut.Core;

/// <summary>One earlier Dictate Prompt turn, kept only in memory.</summary>
/// <param name="SelectedText">The text that was being edited; null on a compose turn.</param>
/// <param name="UserInstruction">What the user said or the quick-action text. A spoken Gemini edit without a transcript uses <see cref="PromptConversationMemory.SpokenPlaceholder"/>.</param>
/// <param name="ModelResponse">The reply that was pasted.</param>
/// <param name="Timestamp">UTC time the turn was recorded.</param>
public sealed record PromptTurn(string? SelectedText, string UserInstruction, string ModelResponse, DateTime Timestamp);

/// <summary>
/// Short-lived conversation memory for Dictate Prompt, matching the Mac's PromptConversationHistory:
/// the last <see cref="MaxTurns"/> online edit turns, dropped after <see cref="InactivityTimeout"/>.
/// Compose turns and Offline Mode never send earlier turns (a new text, or the next patient, must
/// not inherit the previous one). Offline Mode also records nothing.
/// </summary>
public sealed class PromptConversationMemory
{
    /// <summary>How many earlier turns travel with the next request.</summary>
    public const int MaxTurns = 5;

    /// <summary>Silence longer than this forgets the conversation (Mac: 5 minutes).</summary>
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Stored when Gemini heard the instruction as audio and we have no transcript yet
    /// (SpeechService.voiceInstructionPlaceholder).
    /// </summary>
    public const string SpokenPlaceholder = "(voice instruction)";

    private readonly List<PromptTurn> _turns = new();
    private readonly Func<DateTime> _now;
    private DateTime? _last;

    /// <summary>Creates an empty memory. <paramref name="utcNow"/> is for tests; production uses UTC.</summary>
    public PromptConversationMemory(Func<DateTime>? utcNow = null) =>
        _now = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>
    /// True when earlier turns go out with this request. A compose turn starts a new text; Offline
    /// Mode exists for practices, where the next edit may be the next patient.
    /// </summary>
    public static bool Includes(bool isCompose, bool offline) => !isCompose && !offline;

    /// <summary>True when a finished turn is kept. Offline Mode records nothing; compose still does.</summary>
    public static bool Records(bool offline) => !offline;

    /// <summary>Earlier turns to send, newest kept up to <see cref="MaxTurns"/>; empty after expiry.</summary>
    public IReadOnlyList<PromptTurn> TurnsForApi()
    {
        ExpireIfNeeded();
        return _turns.Count == 0 ? Array.Empty<PromptTurn>() : _turns.ToList();
    }

    /// <summary>Adds a finished turn. Empty replies are ignored. Oldest past <see cref="MaxTurns"/> drop.</summary>
    public void Append(string? selectedText, string userInstruction, string modelResponse)
    {
        var reply = (modelResponse ?? "").Trim();
        if (reply.Length == 0) return;
        ExpireIfNeeded();
        _turns.Add(new PromptTurn(selectedText, userInstruction ?? "", reply, _now()));
        if (_turns.Count > MaxTurns) _turns.RemoveRange(0, _turns.Count - MaxTurns);
        _last = _now();
    }

    /// <summary>Forgets every turn (Offline Mode on, or a test).</summary>
    public void Clear()
    {
        _turns.Clear();
        _last = null;
    }

    private void ExpireIfNeeded()
    {
        if (_last is not { } last) return;
        if (_now() - last <= InactivityTimeout) return;
        Log.Info($"PROMPT-HISTORY: expired after {(_now() - last).TotalSeconds:0}s");
        Clear();
    }
}
