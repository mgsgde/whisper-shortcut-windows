namespace WhisperShortcut;

/// <summary>
/// Meetings with their own models (Mac MeetingsSettingsTab: Meeting Transcription Model, Meeting Summary Model). The
/// <see cref="EngineSet"/> meeting step reads the meeting choices on every call, so the Meetings page applies without a
/// restart. Kept apart from TrayApp.cs, which only routes its meeting callbacks through here.
/// </summary>
internal sealed partial class TrayApp
{
    /// <summary>The summary with the meeting summary model; offline with the local language model.</summary>
    private Task<string> SummarizeMeetingWithModelAsync(string text, CancellationToken ct)
    {
        if (_engines.TextEngine(EngineStep.Meeting) == Core.Engine.Local && !OfflineEngine.LlmInstalled(_offline.SelectedLlm))
            throw new InvalidOperationException("The offline language model is not installed.");
        return _engines.Text(EngineStep.Meeting)!.PromptTextAsync(text, Core.MeetingPrompts.SummaryInstruction, Core.MeetingPrompts.SummarySystemPrompt, ct);
    }
}
