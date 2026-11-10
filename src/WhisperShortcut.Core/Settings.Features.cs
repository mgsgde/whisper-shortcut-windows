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

    /// <summary>
    /// Where meeting chunks are transcribed, once the user picked it (<see cref="MeetingSpeechEngineExplicit"/>). Until then
    /// meetings follow the dictation engine: see <see cref="ConfiguredMeetingSpeechEngine"/>.
    /// </summary>
    public ModelSelection MeetingTranscription { get; set; } = ModelSelection.Default(ModelStep.Transcription);

    /// <summary>
    /// The user picked the meeting engine in Einstellungen → Meetings (Mac: the meetings model key is set). False = meetings
    /// follow the dictation engine, so an offline-dictation user's meeting audio never goes to the cloud.
    /// </summary>
    public bool MeetingSpeechEngineExplicit { get; set; }

    /// <summary>The meeting engine before Offline-Modus: the user's pick, else the dictation engine.</summary>
    [JsonIgnore]
    public Engine ConfiguredMeetingSpeechEngine => MeetingSpeechEngineExplicit ? MeetingTranscription.Engine : Dictate.Engine;

    /// <summary>
    /// Where the meeting summary is written and with which models (Mac default Gemini 3.5 Flash-Lite). Missing in an older file
    /// = the Dictate Prompt choice. Named like the engine key (meetingTextEngine): <see cref="MeetingSummary"/> is the on switch.
    /// </summary>
    public ModelSelection MeetingText { get; set; } = ModelSelection.Default(ModelStep.Text) with { Gemini = MeetingOptions.DefaultSummaryModel };

    /// <summary>Longest meeting chunk in seconds, one of <see cref="MeetingOptions.ChunkSeconds"/> (Mac Chunk Interval).</summary>
    public int MeetingChunkSeconds { get; set; } = MeetingOptions.DefaultChunkSeconds;

    /// <summary>Ask „Langes Meeting“ after this many minutes; 0 = never (Mac Meeting Safeguard).</summary>
    public int MeetingSafeguardMinutes { get; set; } = MeetingOptions.DefaultSafeguardMinutes;

    /// <summary>The meeting speech engine actually used: Offline-Modus overrides it with <see cref="Engine.Local"/>.</summary>
    [JsonIgnore]
    public Engine EffectiveMeetingSpeechEngine => OfflineMode ? Engine.Local : ConfiguredMeetingSpeechEngine;

    /// <summary>The meeting summary engine actually used: Offline-Modus overrides it with <see cref="Engine.Local"/>.</summary>
    [JsonIgnore]
    public Engine EffectiveMeetingTextEngine => MeetingText.Effective(OfflineMode).Engine;

    // ---------------- Chat (Mac ChatSettingsTab) ----------------

    /// <summary>Hotkey that opens and closes the chat window.</summary>
    public string ChatHotkey { get; set; } = "";

    /// <summary>Chat hotkey on. Use <see cref="ActiveChatHotkey"/> to register.</summary>
    public bool ChatHotkeyEnabled { get; set; }

    /// <summary>The chat key to register, "" when off.</summary>
    [JsonIgnore]
    public string ActiveChatHotkey => Active(ChatHotkey, ChatHotkeyEnabled);

    /// <summary>Who answers in the chat and with which models (Mac default Gemini 3.8 Flash). Missing in an older file = the Dictate Prompt choice.</summary>
    public ModelSelection Chat { get; set; } = ModelSelection.Default(ModelStep.Text);

    /// <summary>The user's own chat instruction. Empty = <see cref="ChatPrompts.DefaultSystemPrompt"/>.</summary>
    public string ChatSystemPrompt { get; set; } = "";

    /// <summary>Gemini answers with Google Search grounding and shows its sources (Mac: web search on by default).</summary>
    public bool ChatWebSearch { get; set; } = true;

    /// <summary>Mac chatCloseOnFocusLoss: the chat closes when another app comes to the front (on by default; /pin keeps it).</summary>
    public bool ChatCloseOnFocusLoss { get; set; } = true;

    /// <summary>Who answers in practice and with which model id ("local" on this PC): Offline-Modus overrides the provider.</summary>
    [JsonIgnore]
    public (Engine Engine, string Model) EffectiveChat => Chat.Effective(OfflineMode);

    private void LoadFeatures(Dto dto)
    {
        ReadAloudVoiceSystem = ReadAloud.NormalizeVoice(TtsProvider.System, dto.ReadAloudVoiceSystem);
        ReadAloudRewritePrompt = CustomOrEmpty(dto.ReadAloudRewritePrompt, ReadAloud.DefaultRewritePrompt);

        // Before the Meetings page, meetings used the Diktieren and Dictate Prompt providers: an update keeps that.
        MeetingTranscription = ModelSelection.Load(ModelStep.Transcription, dto.MeetingSpeechEngine, dto.MeetingTranscriptionModel,
            dto.MeetingOpenAiTranscriptionModel, Dictate);
        // A file from before the flag always stored an engine, the factory default Gemini included, so "chosen" cannot be told
        // from "never chosen" there: Gemini reads as never chosen (follows dictation), any other value was a pick.
        MeetingSpeechEngineExplicit = dto.MeetingSpeechEngineExplicit
            ?? (dto.MeetingSpeechEngine != null && MeetingTranscription.Engine != Engine.Gemini);
        MeetingText = ModelSelection.Load(ModelStep.Text, dto.MeetingTextEngine, dto.MeetingSummaryModel, dto.MeetingOpenAiSummaryModel,
            Prompt with { Gemini = MeetingOptions.DefaultSummaryModel });
        MeetingChunkSeconds = MeetingOptions.NormalizeChunkSeconds(dto.MeetingChunkSeconds);
        MeetingSafeguardMinutes = MeetingOptions.NormalizeSafeguardMinutes(dto.MeetingSafeguardMinutes);

        if (dto.ChatHotkey != null) ChatHotkey = dto.ChatHotkey;
        ChatHotkeyEnabled = dto.ChatHotkeyEnabled ?? true;
        // An update adds the chat key to a file that never had it. It must not take a key the user already uses.
        if (dto.ChatHotkey == null && HotkeySlots.Clashes(this, HotkeyAction.Chat)) ChatHotkeyEnabled = false;
        Chat = ModelSelection.Load(ModelStep.Text, dto.ChatEngine, dto.ChatModel, dto.ChatOpenAiModel, Prompt with { Gemini = DefaultPromptModel });
        ChatSystemPrompt = CustomOrEmpty(dto.ChatSystemPrompt, ChatPrompts.DefaultSystemPrompt);
        ChatWebSearch = dto.ChatWebSearch ?? true;
        ChatCloseOnFocusLoss = dto.ChatCloseOnFocusLoss ?? true;
    }

    private void SaveFeatures(Dto dto)
    {
        dto.ReadAloudVoiceSystem = ReadAloudVoiceSystem;
        dto.ReadAloudRewritePrompt = ReadAloudRewritePrompt;
        dto.MeetingSpeechEngine = Choices.Engine.Token(MeetingTranscription.Engine);
        dto.MeetingSpeechEngineExplicit = MeetingSpeechEngineExplicit;
        dto.MeetingTranscriptionModel = MeetingTranscription.Gemini;
        dto.MeetingOpenAiTranscriptionModel = MeetingTranscription.OpenAi;
        dto.MeetingTextEngine = Choices.Engine.Token(MeetingText.Engine);
        dto.MeetingSummaryModel = MeetingText.Gemini;
        dto.MeetingOpenAiSummaryModel = MeetingText.OpenAi;
        dto.MeetingChunkSeconds = MeetingChunkSeconds;
        dto.MeetingSafeguardMinutes = MeetingSafeguardMinutes;
        dto.ChatHotkey = ChatHotkey;
        dto.ChatHotkeyEnabled = ChatHotkeyEnabled;
        dto.ChatEngine = Choices.Engine.Token(Chat.Engine);
        dto.ChatModel = Chat.Gemini;
        dto.ChatOpenAiModel = Chat.OpenAi;
        dto.ChatSystemPrompt = ChatSystemPrompt;
        dto.ChatWebSearch = ChatWebSearch;
        dto.ChatCloseOnFocusLoss = ChatCloseOnFocusLoss;
    }

    private sealed partial class Dto
    {
        [JsonPropertyName("readAloudVoiceSystem")] public string? ReadAloudVoiceSystem { get; set; }
        [JsonPropertyName("readAloudRewritePrompt")] public string? ReadAloudRewritePrompt { get; set; }
        [JsonPropertyName("meetingSpeechEngine")] public string? MeetingSpeechEngine { get; set; }
        [JsonPropertyName("meetingSpeechEngineExplicit")] public bool? MeetingSpeechEngineExplicit { get; set; }
        [JsonPropertyName("meetingTranscriptionModel")] public string? MeetingTranscriptionModel { get; set; }
        [JsonPropertyName("meetingOpenAiTranscriptionModel")] public string? MeetingOpenAiTranscriptionModel { get; set; }
        [JsonPropertyName("meetingTextEngine")] public string? MeetingTextEngine { get; set; }
        [JsonPropertyName("meetingSummaryModel")] public string? MeetingSummaryModel { get; set; }
        [JsonPropertyName("meetingOpenAiSummaryModel")] public string? MeetingOpenAiSummaryModel { get; set; }
        [JsonPropertyName("meetingChunkSeconds")] public int? MeetingChunkSeconds { get; set; }
        [JsonPropertyName("meetingSafeguardMinutes")] public int? MeetingSafeguardMinutes { get; set; }
        [JsonPropertyName("chatHotkey")] public string? ChatHotkey { get; set; }
        [JsonPropertyName("chatHotkeyEnabled")] public bool? ChatHotkeyEnabled { get; set; }
        [JsonPropertyName("chatEngine")] public string? ChatEngine { get; set; }
        [JsonPropertyName("chatModel")] public string? ChatModel { get; set; }
        [JsonPropertyName("chatOpenAiModel")] public string? ChatOpenAiModel { get; set; }
        [JsonPropertyName("chatSystemPrompt")] public string? ChatSystemPrompt { get; set; }
        [JsonPropertyName("chatWebSearch")] public bool? ChatWebSearch { get; set; }
        [JsonPropertyName("chatCloseOnFocusLoss")] public bool? ChatCloseOnFocusLoss { get; set; }
    }
}
