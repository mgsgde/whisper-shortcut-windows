using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

// Settings added with step 10 of docs/plan-mac-angleich.md (Vorlesen, Meetings, Chat). In their own file so the main settings
// file only carries two hook lines: LoadFeatures in Load and SaveFeatures in SaveCore.
public sealed partial class Settings
{
    /// <summary>Chosen Windows voice for Read Aloud (its name); "" = the voice of the Windows language.</summary>
    public string ReadAloudVoiceSystem { get; set; } = "";

    /// <summary>The user's own Smart Rewrite instruction (Mac: Rewrite Prompt). Empty = <see cref="ReadAloud.DefaultRewritePrompt"/>.</summary>
    public string ReadAloudRewritePrompt { get; set; } = "";

    /// <summary>The rewrite instruction actually sent.</summary>
    [JsonIgnore]
    public string EffectiveReadAloudRewritePrompt =>
        ReadAloudRewritePrompt.Trim().Length > 0 ? ReadAloudRewritePrompt : ReadAloud.DefaultRewritePrompt;

    /// <summary>The model Vorlesen speaks with: offline always the Windows voices (Mac: automatic in Offline Mode).</summary>
    [JsonIgnore]
    public TtsModel EffectiveReadAloudModel => ReadAloud.Effective(ReadAloudModel, OfflineMode);

    // ---------------- Meetings (Mac MeetingsSettingsTab) ----------------

    /// <summary>Where meeting chunks are transcribed. Missing in an older file = the dictation choice, as meetings ran before.</summary>
    public Engine MeetingSpeechEngine { get; set; } = Engine.Gemini;

    /// <summary>Gemini model for meeting transcription.</summary>
    public string MeetingTranscriptionModel { get; set; } = DefaultTranscriptionModel;

    /// <summary>OpenAI model for meeting transcription.</summary>
    public string MeetingOpenAiTranscriptionModel { get; set; } = OpenAiModels.DefaultTranscriptionModel;

    /// <summary>Where the meeting summary is written. Missing in an older file = the Dictate Prompt choice.</summary>
    public Engine MeetingTextEngine { get; set; } = Engine.Gemini;

    /// <summary>Gemini model for the meeting summary (Mac default Gemini 3.5 Flash-Lite).</summary>
    public string MeetingSummaryModel { get; set; } = MeetingOptions.DefaultSummaryModel;

    /// <summary>OpenAI model for the meeting summary.</summary>
    public string MeetingOpenAiSummaryModel { get; set; } = OpenAiModels.DefaultPromptModel;

    /// <summary>Longest meeting chunk in seconds, one of <see cref="MeetingOptions.ChunkSeconds"/> (Mac Chunk Interval).</summary>
    public int MeetingChunkSeconds { get; set; } = MeetingOptions.DefaultChunkSeconds;

    /// <summary>Ask „Langes Meeting“ after this many minutes; 0 = never (Mac Meeting Safeguard).</summary>
    public int MeetingSafeguardMinutes { get; set; } = MeetingOptions.DefaultSafeguardMinutes;

    /// <summary>The meeting speech engine actually used: Offline-Modus overrides it with <see cref="Engine.Local"/>.</summary>
    [JsonIgnore]
    public Engine EffectiveMeetingSpeechEngine => OfflineMode ? Engine.Local : MeetingSpeechEngine;

    /// <summary>The meeting summary engine actually used: Offline-Modus overrides it with <see cref="Engine.Local"/>.</summary>
    [JsonIgnore]
    public Engine EffectiveMeetingTextEngine => OfflineMode ? Engine.Local : MeetingTextEngine;

    private void LoadFeatures(Dto dto)
    {
        ReadAloudVoiceSystem = ReadAloud.NormalizeVoice(TtsProvider.System, dto.ReadAloudVoiceSystem);
        ReadAloudRewritePrompt = CustomOrEmpty(dto.ReadAloudRewritePrompt, ReadAloud.DefaultRewritePrompt);

        // Before the Meetings page, meetings used the Diktieren and Dictate Prompt providers: an update keeps that.
        MeetingSpeechEngine = dto.MeetingSpeechEngine != null ? ParseEngine(dto.MeetingSpeechEngine) : SpeechEngine;
        MeetingTranscriptionModel = GeminiModels.NormalizeTranscription(dto.MeetingTranscriptionModel ?? TranscriptionModel);
        MeetingOpenAiTranscriptionModel = OpenAiModels.NormalizeTranscription(dto.MeetingOpenAiTranscriptionModel ?? OpenAiTranscriptionModel);
        MeetingTextEngine = dto.MeetingTextEngine != null ? ParseEngine(dto.MeetingTextEngine) : TextEngine;
        MeetingSummaryModel = GeminiModels.NormalizePrompt(dto.MeetingSummaryModel ?? MeetingOptions.DefaultSummaryModel);
        MeetingOpenAiSummaryModel = OpenAiModels.NormalizePrompt(dto.MeetingOpenAiSummaryModel ?? OpenAiPromptModel);
        MeetingChunkSeconds = MeetingOptions.NormalizeChunkSeconds(dto.MeetingChunkSeconds);
        MeetingSafeguardMinutes = MeetingOptions.NormalizeSafeguardMinutes(dto.MeetingSafeguardMinutes);
    }

    private void SaveFeatures(Dto dto)
    {
        dto.ReadAloudVoiceSystem = ReadAloudVoiceSystem;
        dto.ReadAloudRewritePrompt = ReadAloudRewritePrompt;
        dto.MeetingSpeechEngine = EngineName(MeetingSpeechEngine);
        dto.MeetingTranscriptionModel = MeetingTranscriptionModel;
        dto.MeetingOpenAiTranscriptionModel = MeetingOpenAiTranscriptionModel;
        dto.MeetingTextEngine = EngineName(MeetingTextEngine);
        dto.MeetingSummaryModel = MeetingSummaryModel;
        dto.MeetingOpenAiSummaryModel = MeetingOpenAiSummaryModel;
        dto.MeetingChunkSeconds = MeetingChunkSeconds;
        dto.MeetingSafeguardMinutes = MeetingSafeguardMinutes;
    }

    private sealed partial class Dto
    {
        [JsonPropertyName("readAloudVoiceSystem")] public string? ReadAloudVoiceSystem { get; set; }
        [JsonPropertyName("readAloudRewritePrompt")] public string? ReadAloudRewritePrompt { get; set; }
        [JsonPropertyName("meetingSpeechEngine")] public string? MeetingSpeechEngine { get; set; }
        [JsonPropertyName("meetingTranscriptionModel")] public string? MeetingTranscriptionModel { get; set; }
        [JsonPropertyName("meetingOpenAiTranscriptionModel")] public string? MeetingOpenAiTranscriptionModel { get; set; }
        [JsonPropertyName("meetingTextEngine")] public string? MeetingTextEngine { get; set; }
        [JsonPropertyName("meetingSummaryModel")] public string? MeetingSummaryModel { get; set; }
        [JsonPropertyName("meetingOpenAiSummaryModel")] public string? MeetingOpenAiSummaryModel { get; set; }
        [JsonPropertyName("meetingChunkSeconds")] public int? MeetingChunkSeconds { get; set; }
        [JsonPropertyName("meetingSafeguardMinutes")] public int? MeetingSafeguardMinutes { get; set; }
    }
}
