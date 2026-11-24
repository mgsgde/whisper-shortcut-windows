namespace WhisperShortcut;

internal sealed partial class TrayApp
{
    /// <summary>What <see cref="Core.DictationPipeline"/> takes from the app: the engines, the settings values, the prompt memory.</summary>
    private sealed class PipelineHost(TrayApp app) : Core.IPipelineHost
    {
        public bool FakeGemini => app._fakeGemini;
        public string PromptModeSystemPrompt => app._settings.PromptModeSystemPrompt;
        public string DictatePromptTemplate => app._settings.DictatePromptTemplate;
        public Core.Engine TextEngine => app._settings.EffectiveTextEngine;
        public IReadOnlyList<string> QuickActions => app._settings.QuickActions;
        public double PromptInlineLimitSeconds => MicAudioSource.PromptLimit.TotalSeconds;

        public Task<string> TranscribeAsync(Core.TranscribePurpose purpose, byte[] wav, CancellationToken ct) =>
            app.TranscribeAsync(purpose, wav, ct);

        public Core.PunctuationMode PunctuationFor(Core.TranscribePurpose purpose) => app.PunctuationFor(purpose);
        public string Backstops(Core.TranscribePurpose purpose, string raw) => app.Backstops(purpose, raw);

        /// <summary>Says "loading" while the local model is not up yet.</summary>
        public Core.ITextProvider PromptProvider(IProgress<string> progress)
        {
            if (app._settings.EffectiveTextEngine == Core.Engine.Local && !app._offline.LlmReady)
                progress.Report(Core.UserCopy.LoadingLlm);
            return app._engines.Text(EngineStep.Prompt)!;
        }

        public Task<string> PromptWithAudioAsync(byte[] wav, string? selection, string systemPrompt, Core.ScreenshotImage? image,
            IReadOnlyList<Core.PromptTurn>? history, CancellationToken ct) =>
            app._gemini.PromptAsync(wav, selection, systemPrompt, ct, history, image);

        public IReadOnlyList<Core.PromptTurn>? HistoryFor(string? selection) => app.HistoryFor(selection);

        public void RememberPrompt(string? selection, string instruction, string response, string? reportInstruction = null) =>
            app.RememberPrompt(selection, instruction, response, reportInstruction);
    }
}
