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

    // ---------------- Chat (Mac ChatSettingsTab) ----------------

    /// <summary>
    /// New-install key for opening and closing the chat. The Mac uses ⌥Space; on Windows Alt+Space opens every window's system
    /// menu, so the chat takes the free Mac digit 6 with Alt like the other shortcuts.
    /// </summary>
    public const string DefaultChatHotkey = "Alt+6";

    /// <summary>Hotkey that opens and closes the chat window.</summary>
    public string ChatHotkey { get; set; } = DefaultChatHotkey;

    /// <summary>Chat hotkey on. Use <see cref="ActiveChatHotkey"/> to register.</summary>
    public bool ChatHotkeyEnabled { get; set; } = true;

    /// <summary>The chat key to register, "" when off.</summary>
    [JsonIgnore]
    public string ActiveChatHotkey => Active(ChatHotkey, ChatHotkeyEnabled);

    /// <summary>Who answers in the chat. Missing in an older file = the Dictate Prompt choice.</summary>
    public Engine ChatEngine { get; set; } = Engine.Gemini;

    /// <summary>Gemini chat model (Mac default Gemini 3.8 Flash).</summary>
    public string ChatModel { get; set; } = DefaultPromptModel;

    /// <summary>OpenAI chat model.</summary>
    public string ChatOpenAiModel { get; set; } = OpenAiModels.DefaultPromptModel;

    /// <summary>The user's own chat instruction. Empty = <see cref="ChatPrompts.DefaultSystemPrompt"/>.</summary>
    public string ChatSystemPrompt { get; set; } = "";

    /// <summary>Gemini answers with Google Search grounding and shows its sources (Mac: web search on by default).</summary>
    public bool ChatWebSearch { get; set; } = true;

    /// <summary>Mac chatCloseOnFocusLoss: the chat closes when another app comes to the front (on by default; /pin keeps it).</summary>
    public bool ChatCloseOnFocusLoss { get; set; } = true;

    /// <summary>Who answers in practice: Offline-Modus overrides the choice with <see cref="Engine.Local"/>.</summary>
    [JsonIgnore]
    public Engine EffectiveChatEngine => OfflineMode ? Engine.Local : ChatEngine;

    /// <summary>The model id for <see cref="EffectiveChatEngine"/> ("local" on this PC).</summary>
    [JsonIgnore]
    public string EffectiveChatModel => EffectiveChatEngine switch
    {
        Engine.Gemini => ChatModel,
        Engine.OpenAI => ChatOpenAiModel,
        _ => ModelPickers.LocalId,
    };

    // True when the chat key equals any other stored key (app keys and template keys); empty keys never clash.
    private bool ChatKeyClashes()
    {
        if (string.IsNullOrWhiteSpace(ChatHotkey)) return false;
        var others = new List<string>
        {
            DictateHotkey, PromptHotkey, GlossaryHotkey, ScreenshotHotkey, LiveDictateHotkey, OpenSettingsHotkey,
            MeetingHotkey, TemplateHotkey, ReadAloudHotkey,
        };
        others.AddRange(Templates.Select(t => t.Hotkey));
        return others.Any(k => SameKey(k, ChatHotkey));
    }

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

        if (dto.ChatHotkey != null) ChatHotkey = dto.ChatHotkey;
        ChatHotkeyEnabled = dto.ChatHotkeyEnabled ?? true;
        // An update adds the chat key to a file that never had it. It must not take a key the user already uses.
        if (dto.ChatHotkey == null && ChatKeyClashes()) ChatHotkeyEnabled = false;
        ChatEngine = dto.ChatEngine != null ? ParseEngine(dto.ChatEngine) : TextEngine;
        ChatModel = GeminiModels.NormalizePrompt(dto.ChatModel ?? DefaultPromptModel);
        ChatOpenAiModel = OpenAiModels.NormalizePrompt(dto.ChatOpenAiModel ?? OpenAiPromptModel);
        ChatSystemPrompt = CustomOrEmpty(dto.ChatSystemPrompt, ChatPrompts.DefaultSystemPrompt);
        ChatWebSearch = dto.ChatWebSearch ?? true;
        ChatCloseOnFocusLoss = dto.ChatCloseOnFocusLoss ?? true;
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
        dto.ChatHotkey = ChatHotkey;
        dto.ChatHotkeyEnabled = ChatHotkeyEnabled;
        dto.ChatEngine = EngineName(ChatEngine);
        dto.ChatModel = ChatModel;
        dto.ChatOpenAiModel = ChatOpenAiModel;
        dto.ChatSystemPrompt = ChatSystemPrompt;
        dto.ChatWebSearch = ChatWebSearch;
        dto.ChatCloseOnFocusLoss = ChatCloseOnFocusLoss;
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
