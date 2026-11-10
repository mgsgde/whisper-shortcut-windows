namespace WhisperShortcut;

/// <summary>Which step of the app asks for a provider; each step has its own engine setting and its own cloud models.</summary>
internal enum EngineStep
{
    /// <summary>Dictate, templates, live pieces: the speech engine.</summary>
    Dictation,
    /// <summary>Dictate Prompt and quick instructions: the speech engine (the instruction) and the text engine.</summary>
    Prompt,
    /// <summary>Meetings: the meeting transcription engine, the meeting summary engine (Settings → Meetings).</summary>
    Meeting,
    /// <summary>Chat and the read-aloud rewrite: the chat engine, with the chat models.</summary>
    Chat,
}

/// <summary>
/// What a step still lacks before it can run. The caller words the message; the order it reads the fields in is
/// models, then CPU, then key (what a hotkey press has always checked).
/// </summary>
/// <param name="MissingKey">"Gemini" or "OpenAI" when a cloud engine the step uses has no key, else null.</param>
/// <param name="SpeechModelMissing">The step uses Parakeet and it is not downloaded.</param>
/// <param name="LlmModelMissing">The step uses the local language model and it is not downloaded.</param>
/// <param name="LlmCpuUnsupported">The step uses the local language model and this CPU cannot run it.</param>
internal readonly record struct EngineReadiness(string? MissingKey, bool SpeechModelMissing, bool LlmModelMissing, bool LlmCpuUnsupported)
{
    public bool ModelsMissing => SpeechModelMissing || LlmModelMissing;
}

/// <summary>
/// Picks the speech and text provider for a step from the settings (Local, Gemini or OpenAI, as the step's effective
/// engine says) and says whether the step can run. The only place that maps an <see cref="Core.Engine"/> to a client.
/// The cloud clients of the meeting and chat steps read their own model choices on every call, so the settings pages
/// apply without a restart; they are created on first use.
/// </summary>
internal sealed class EngineSet
{
    private sealed record Cloud(Core.GeminiClient Gemini, Core.OpenAiClient OpenAi);

    private readonly Core.Settings _settings;
    private readonly LocalEngines? _local;
    private readonly Lazy<Cloud> _dictation;
    private readonly Lazy<Cloud> _meeting;
    private readonly Lazy<Cloud> _chat;

    /// <param name="local">The on-device provider; null where the local engine is never used (read-aloud rewrite).</param>
    /// <param name="gemini">The app's Gemini client for dictation and prompts; null creates one.</param>
    /// <param name="openAi">The app's OpenAI client for dictation and prompts; null creates one.</param>
    public EngineSet(Core.Settings settings, LocalEngines? local, Core.GeminiClient? gemini = null, Core.OpenAiClient? openAi = null)
    {
        _settings = settings;
        _local = local;
        _dictation = new(() => new Cloud(gemini ?? new Core.GeminiClient(settings), openAi ?? new Core.OpenAiClient(settings)));
        _meeting = new(() => new Cloud(
            new Core.GeminiClient(() => settings.ApiKey, () => settings.MeetingTranscriptionModel, () => settings.MeetingSummaryModel, null, () => settings.DictateSystemPrompt),
            new Core.OpenAiClient(() => settings.OpenAiKey, () => settings.MeetingOpenAiTranscriptionModel, () => settings.MeetingOpenAiSummaryModel)));
        // Mac maybeRewriteForSpeech uses the chat model (Settings → Chat).
        _chat = new(() => new Cloud(
            new Core.GeminiClient(() => settings.ApiKey, () => settings.TranscriptionModel, () => settings.ChatModel),
            new Core.OpenAiClient(() => settings.OpenAiKey, () => settings.OpenAiTranscriptionModel, () => settings.ChatOpenAiModel)));
    }

    /// <summary>The engine this step's speech recognition runs on.</summary>
    public Core.Engine SpeechEngine(EngineStep step) =>
        step == EngineStep.Meeting ? _settings.EffectiveMeetingSpeechEngine : _settings.EffectiveSpeechEngine;

    /// <summary>The engine this step's text processing runs on.</summary>
    public Core.Engine TextEngine(EngineStep step) => step switch
    {
        EngineStep.Meeting => _settings.EffectiveMeetingTextEngine,
        EngineStep.Chat => _settings.EffectiveChatEngine,
        _ => _settings.EffectiveTextEngine,
    };

    /// <summary>The on-device provider (Parakeet, local language model); throws in a set built without one.</summary>
    public LocalEngines Local => _local ?? throw new InvalidOperationException("No local engine in this set.");

    private Cloud Pair(EngineStep step) => step switch
    {
        EngineStep.Meeting => _meeting.Value,
        EngineStep.Chat => _chat.Value,
        _ => _dictation.Value,
    };

    /// <summary>The speech provider of the step's engine.</summary>
    public Core.ISpeechProvider Speech(EngineStep step) => SpeechEngine(step) switch
    {
        Core.Engine.Local => Local,
        Core.Engine.OpenAI => Pair(step).OpenAi,
        _ => Pair(step).Gemini,
    };

    /// <summary>The text provider of the step's engine; null for the local engine in a set built without one (read-aloud rewrite).</summary>
    public Core.ITextProvider? Text(EngineStep step) => TextEngine(step) switch
    {
        Core.Engine.Local => _local,
        Core.Engine.OpenAI => Pair(step).OpenAi,
        _ => Pair(step).Gemini,
    };

    private (Core.Engine? Speech, Core.Engine? Text) EnginesUsed(EngineStep step) => step switch
    {
        EngineStep.Dictation => (SpeechEngine(step), null),
        EngineStep.Prompt => (SpeechEngine(step), TextEngine(step)),
        EngineStep.Meeting => (SpeechEngine(step), null),
        _ => (null, TextEngine(step)),
    };

    /// <summary>Opens the connection of every cloud engine the step uses, so the first request after Stop skips the handshake.</summary>
    public void Prewarm(EngineStep step)
    {
        var (speech, text) = EnginesUsed(step);
        foreach (var engine in new[] { speech, text }.OfType<Core.Engine>().Distinct())
        {
            if (engine == Core.Engine.Gemini) _ = Pair(step).Gemini.PrewarmConnection();
            else if (engine == Core.Engine.OpenAI) _ = Pair(step).OpenAi.PrewarmConnection();
        }
    }

    /// <summary>Whether the step can run now. <paramref name="checkKeys"/> false skips the key check (test runs).</summary>
    public EngineReadiness Check(EngineStep step, bool checkKeys = true)
    {
        var (speech, text) = EnginesUsed(step);
        string? missingKey = null;
        if (checkKeys)
        {
            // Gemini's key is reported first when both are missing.
            foreach (var engine in new[] { Core.Engine.Gemini, Core.Engine.OpenAI })
            {
                if (speech != engine && text != engine) continue;
                missingKey = _settings.MissingKeyProvider(engine);
                if (missingKey != null) break;
            }
        }
        bool localSpeech = speech == Core.Engine.Local;
        bool localText = text == Core.Engine.Local && _local != null;
        return new EngineReadiness(missingKey,
            SpeechModelMissing: localSpeech && !OfflineEngine.SpeechInstalled,
            LlmModelMissing: localText && !OfflineEngine.LlmInstalled(_local!.Offline.SelectedLlm),
            LlmCpuUnsupported: localText && OfflineEngine.LlmUnsupportedCpu);
    }
}
