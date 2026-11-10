namespace WhisperShortcut;

/// <summary>
/// The engine that runs on this PC (Parakeet for speech, the local language model for text) behind the same two
/// interfaces as the cloud clients, so a caller picks a provider by <see cref="Core.Engine"/> and calls it.
/// Needs no key, no connection and ignores a screenshot or earlier turns (the local model gets neither).
/// </summary>
internal sealed class LocalEngines(OfflineEngine offline) : Core.ISpeechProvider, Core.ITextProvider
{
    public OfflineEngine Offline { get; } = offline;

    public Task PrewarmConnection() => Task.CompletedTask;

    /// <summary>Parakeet's pieces: 30 s, because its cost grows faster than the length.</summary>
    public IReadOnlyList<byte[]> SplitForUpload(byte[] wav) => Core.WavChunker.SplitForLocal(wav);

    // Parakeet gets the glossary as hotwords from the settings, not per call.
    public Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, CancellationToken ct = default) =>
        TranscribeAsync(wav, Core.PunctuationMode.Automatic, ct);

    public Task<string> TranscribeAsync(byte[] wav, IReadOnlyList<string> glossary, bool sendKeywords, CancellationToken ct = default) =>
        TranscribeAsync(wav, Core.PunctuationMode.Automatic, ct);

    /// <summary>
    /// Parakeet, in 30-second pieces above 40 s, one at a time (the recognizer is serialized anyway).
    /// Spoken "Doppelpunkt" / "neuer Absatz" become punctuation; Gemini does that itself.
    /// </summary>
    public async Task<string> TranscribeAsync(byte[] wav, Core.PunctuationMode punctuation, CancellationToken ct)
    {
        if (punctuation != Core.PunctuationMode.SpokenOnly)
        {
            var joined = await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
                (chunk, c) => Offline.TranscribeAsync(chunk, c), maxParallel: 1, ct);
            return Core.SpokenPunctuation.Apply(joined, punctuation);
        }
        // "Nur gesprochen" (Mac #339/#347): each piece keeps Parakeet's marks only where the token timings show speech
        // time behind them; a piece without usable timings falls back to the plain strip. Then, as on the Mac, the
        // joined text loses only semicolons (timedMarks) before the spoken commands are applied.
        var spoken = await Core.ChunkedTranscription.TranscribeAsync(Core.WavChunker.SplitForLocal(wav),
            async (chunk, c) =>
            {
                var (text, timed) = await Offline.TranscribeSpokenMarksAsync(chunk, c);
                return timed ? text : Core.SpokenPunctuation.StripModelPunctuation(text);
            }, maxParallel: 1, ct);
        return Core.SpokenPunctuation.Apply(Core.SpokenPunctuation.StripModelPunctuation(spoken, timedMarks: true), punctuation);
    }

    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct = default, IReadOnlyList<Core.PromptTurn>? history = null) =>
        Offline.PromptAsync(selection, instruction, customSystemPrompt, ct);

    public Task<string> PromptTextAsync(string? selection, string instruction, string? customSystemPrompt,
        CancellationToken ct, IReadOnlyList<Core.PromptTurn>? history, Core.ScreenshotImage? image) =>
        Offline.PromptAsync(selection, instruction, customSystemPrompt, ct);

    public Task<string> CompleteTextAsync(string systemPrompt, string userText, Core.ScreenshotImage? image, CancellationToken ct = default) =>
        Offline.CompleteAsync(systemPrompt, userText, Core.LocalLlmClient.TemplateMaxTokens, ct);
}
