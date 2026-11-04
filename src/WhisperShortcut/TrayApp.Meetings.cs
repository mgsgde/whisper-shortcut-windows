namespace WhisperShortcut;

/// <summary>
/// Meetings with their own models (Mac MeetingsSettingsTab: Meeting Transcription Model, Meeting Summary Model). Clients that
/// read the meeting choices on every call, so the Meetings page applies without a restart. Kept apart from TrayApp.cs, which
/// only routes its meeting callbacks through here.
/// </summary>
internal sealed partial class TrayApp
{
    private Core.GeminiClient? _meetingGemini;
    private Core.OpenAiClient? _meetingOpenAi;

    private Core.GeminiClient MeetingGemini => _meetingGemini ??= new Core.GeminiClient(() => _settings.ApiKey,
        () => _settings.MeetingTranscriptionModel, () => _settings.MeetingSummaryModel, null, () => _settings.DictateSystemPrompt);

    private Core.OpenAiClient MeetingOpenAi => _meetingOpenAi ??= new Core.OpenAiClient(() => _settings.OpenAiKey,
        () => _settings.MeetingOpenAiTranscriptionModel, () => _settings.MeetingOpenAiSummaryModel);

    /// <summary>A meeting chunk with the meeting transcription model (same glossary and cleanup as dictation).</summary>
    private Task<string> TranscribeMeetingRawAsync(byte[] wav, CancellationToken ct) => _settings.EffectiveMeetingSpeechEngine switch
    {
        Core.Engine.Local => TranscribeOfflineAsync(wav, ct, Core.PunctuationMode.Automatic),
        Core.Engine.OpenAI => TranscribeOnlineAsync(MeetingOpenAi, wav, ct, meetingChunk: true),
        _ => TranscribeOnlineAsync(MeetingGemini, wav, ct, meetingChunk: true),
    };

    /// <summary>The summary with the meeting summary model; offline with the local language model.</summary>
    private Task<string> SummarizeMeetingWithModelAsync(string text, CancellationToken ct) => _settings.EffectiveMeetingTextEngine switch
    {
        Core.Engine.Local when !OfflineEngine.LlmInstalled(_offline.SelectedLlm) =>
            throw new InvalidOperationException("The offline language model is not installed."),
        Core.Engine.Local => _offline.PromptAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
        Core.Engine.OpenAI => MeetingOpenAi.PromptTextAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
        _ => MeetingGemini.PromptTextAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct),
    };

    /// <summary>The provider whose key the meeting transcription lacks, or null.</summary>
    private string? MeetingMissingKey() => _settings.EffectiveMeetingSpeechEngine switch
    {
        Core.Engine.Gemini when string.IsNullOrWhiteSpace(_settings.ApiKey) => "Gemini",
        Core.Engine.OpenAI when string.IsNullOrWhiteSpace(_settings.OpenAiKey) => "OpenAI",
        _ => null,
    };
}
