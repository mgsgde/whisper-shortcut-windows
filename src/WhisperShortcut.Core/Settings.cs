using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

/// <summary>Encrypts the API key at rest. DPAPI implementation lives in the WinForms project.</summary>
public interface ISecretProtector
{
    /// <summary>Returns a storable (text) form of <paramref name="plain"/>.</summary>
    string Protect(string plain);

    /// <summary>Reverses <see cref="Protect"/>; returns null if it cannot be decrypted.</summary>
    string? Unprotect(string stored);
}

/// <summary>Passthrough protector for tests: stores the secret as is.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    /// <inheritdoc />
    public string Protect(string plain) => plain;

    /// <inheritdoc />
    public string? Unprotect(string stored) => stored;
}

/// <summary>User settings, stored as JSON. The API key is written only in protected form.</summary>
public sealed class Settings
{
    /// <summary>Default transcription model.</summary>
    public const string DefaultTranscriptionModel = "gemini-3.1-flash-lite";

    /// <summary>Default Dictate Prompt model.</summary>
    public const string DefaultPromptModel = "gemini-3.8-flash";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    private Settings(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>Default location: %APPDATA%\WhisperShortcut\settings.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperShortcut", "settings.json");

    /// <summary>Gemini API key (plain in memory, protected on disk). Read at call time by GeminiClient.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>OpenAI API key (plain in memory, protected on disk). Read at call time by OpenAiClient.</summary>
    public string OpenAiKey { get; set; } = "";

    /// <summary>Where speech recognition (Dictate) runs. Use <see cref="EffectiveSpeechEngine"/> to decide.</summary>
    public Engine SpeechEngine { get; set; } = Engine.Gemini;

    /// <summary>Where text processing (Dictate Prompt) runs. Use <see cref="EffectiveTextEngine"/> to decide.</summary>
    public Engine TextEngine { get; set; } = Engine.Gemini;

    /// <summary>OpenAI transcription model id.</summary>
    public string OpenAiTranscriptionModel { get; set; } = OpenAiModels.DefaultTranscriptionModel;

    /// <summary>OpenAI Dictate Prompt model id.</summary>
    public string OpenAiPromptModel { get; set; } = OpenAiModels.DefaultPromptModel;

    /// <summary>
    /// The speech engine actually used. <see cref="OfflineMode"/> is the tray's "nothing leaves this PC"
    /// switch and overrides the choice with <see cref="Engine.Local"/>. Not persisted.
    /// </summary>
    [JsonIgnore]
    public Engine EffectiveSpeechEngine => OfflineMode ? Engine.Local : SpeechEngine;

    /// <summary>
    /// The text engine actually used. <see cref="OfflineMode"/> is the tray's "nothing leaves this PC"
    /// switch and overrides the choice with <see cref="Engine.Local"/>. Not persisted.
    /// </summary>
    [JsonIgnore]
    public Engine EffectiveTextEngine => OfflineMode ? Engine.Local : TextEngine;

    /// <summary>Vocabulary terms that bias transcription spelling.</summary>
    public List<string> Glossary { get; set; } = new();

    /// <summary>
    /// The template ("Vorlage") for new texts in Dictate Prompt; appended to the editing prompt and applied only
    /// when nothing is selected. Empty = no template. JSON key "dictatePromptTemplate"; files before 0.5.3 used
    /// "promptSystemPrompt", still read on load.
    /// </summary>
    public string DictatePromptTemplate { get; set; } = "";

    /// <summary>
    /// The user's own transcription instruction for Dictate (Gemini only; offline speech recognition takes no prompt).
    /// Empty = Prompts.DefaultTranscriptionSystemPrompt.
    /// </summary>
    public string DictateSystemPrompt { get; set; } = "";

    /// <summary>
    /// The user's own editing instruction for Dictate Prompt (online and offline).
    /// Empty = Prompts.DefaultPromptModeSystemPrompt. The template for new texts (DictatePromptTemplate) is appended to it.
    /// </summary>
    public string PromptModeSystemPrompt { get; set; } = "";

    /// <summary>Transcription model id.</summary>
    public string TranscriptionModel { get; set; } = DefaultTranscriptionModel;

    /// <summary>Dictate Prompt model id.</summary>
    public string PromptModel { get; set; } = DefaultPromptModel;

    /// <summary>Product name of the microphone to record from; "" = the Windows default device.</summary>
    public string Microphone { get; set; } = "";

    /// <summary>Hotkey for plain dictation.</summary>
    public string DictateHotkey { get; set; } = "Ctrl+Shift+D";

    /// <summary>Hotkey for Dictate Prompt.</summary>
    public string PromptHotkey { get; set; } = "Ctrl+Shift+P";

    /// <summary>Hotkey that appends the selected text to the Glossary.</summary>
    public string GlossaryHotkey { get; set; } = "Ctrl+Shift+K";

    /// <summary>Offline mode: dictation and Dictate Prompt run on this PC (Parakeet + local LLM).</summary>
    public bool OfflineMode { get; set; }

    /// <summary>Id of the chosen local LLM (see <see cref="OfflineCatalog"/>); empty = recommended for this PC.</summary>
    public string OfflineLlmId { get; set; } = "";

    /// <summary>Quick instructions offered while recording (see <see cref="QuickActionList"/>).</summary>
    public List<string> QuickActions { get; set; } = new(QuickActionList.Defaults);

    /// <summary>Paste the result at the cursor with Ctrl+V. Off = the text only goes to the clipboard.</summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>Play the short start/stop/error tones.</summary>
    public bool Sounds { get; set; } = true;

    /// <summary>Look for a new version at start and once a day; only shows a notice, installs nothing by itself.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>Put the previous clipboard text back after pasting (ignored when <see cref="AutoPaste"/> is off).</summary>
    public bool RestoreClipboard { get; set; } = true;

    /// <summary>
    /// "" when <paramref name="text"/> is blank or equals <paramref name="defaultText"/> (ignoring line endings and
    /// surrounding whitespace), otherwise the text trimmed with "\n" line endings. Keeps "default" stored as empty.
    /// </summary>
    public static string CustomOrEmpty(string? text, string defaultText)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = Normalize(text);
        return t == Normalize(defaultText) ? "" : t;

        static string Normalize(string v) => v.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    /// <summary>Loads the settings; a missing or corrupt file yields defaults (a corrupt file is kept as .bak).</summary>
    public static Settings Load(string path, ISecretProtector protector)
    {
        var s = new Settings(path, protector);
        if (!File.Exists(path)) return s;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            if (dto == null) return s;
            if (!string.IsNullOrEmpty(dto.ApiKeyProtected))
            {
                try { s.ApiKey = protector.Unprotect(dto.ApiKeyProtected) ?? ""; }
                catch { s.ApiKey = ""; }
            }
            if (!string.IsNullOrEmpty(dto.OpenAiKeyProtected))
            {
                try { s.OpenAiKey = protector.Unprotect(dto.OpenAiKeyProtected) ?? ""; }
                catch { s.OpenAiKey = ""; }
            }
            s.SpeechEngine = ParseEngine(dto.SpeechEngine);
            s.TextEngine = ParseEngine(dto.TextEngine);
            s.OpenAiTranscriptionModel = OpenAiModels.NormalizeTranscription(dto.OpenAiTranscriptionModel);
            s.OpenAiPromptModel = OpenAiModels.NormalizePrompt(dto.OpenAiPromptModel);
            s.Glossary = dto.Glossary ?? new();
            s.DictatePromptTemplate = dto.DictatePromptTemplate ?? dto.LegacyPromptSystemPrompt ?? "";
            s.DictateSystemPrompt = CustomOrEmpty(dto.DictateSystemPrompt, Prompts.DefaultTranscriptionSystemPrompt);
            s.PromptModeSystemPrompt = CustomOrEmpty(dto.PromptModeSystemPrompt, Prompts.DefaultPromptModeSystemPrompt);
            s.TranscriptionModel = GeminiModels.NormalizeTranscription(dto.TranscriptionModel);
            s.PromptModel = GeminiModels.NormalizePrompt(dto.PromptModel);
            if (!string.IsNullOrWhiteSpace(dto.DictateHotkey)) s.DictateHotkey = dto.DictateHotkey;
            if (!string.IsNullOrWhiteSpace(dto.PromptHotkey)) s.PromptHotkey = dto.PromptHotkey;
            if (!string.IsNullOrWhiteSpace(dto.GlossaryHotkey)) s.GlossaryHotkey = dto.GlossaryHotkey;
            s.Microphone = dto.Microphone ?? "";
            s.OfflineMode = dto.OfflineMode ?? false;
            s.OfflineLlmId = dto.OfflineLlmId ?? "";
            s.AutoPaste = dto.AutoPaste ?? true;
            s.Sounds = dto.Sounds ?? true;
            s.AutoUpdateCheck = dto.AutoUpdateCheck ?? true;
            s.RestoreClipboard = dto.RestoreClipboard ?? true;
            // Missing field = old file, give the defaults; an explicit empty list means the user removed them all.
            s.QuickActions = dto.QuickActions == null || QuickActionList.IsLegacyDefaults(dto.QuickActions)
                ? new(QuickActionList.Defaults)
                : QuickActionList.Clean(dto.QuickActions);
        }
        catch (Exception ex)
        {
            Log.Error("settings.json unreadable, using defaults", ex);
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        }
        return s;
    }

    // Unknown or missing = Gemini, the engine every old settings file used.
    private static Engine ParseEngine(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "local" => Engine.Local,
        "openai" => Engine.OpenAI,
        _ => Engine.Gemini,
    };

    private static string EngineName(Engine e) => e switch
    {
        Engine.Local => "local",
        Engine.OpenAI => "openai",
        _ => "gemini",
    };

    /// <summary>Writes the settings to the path given at <see cref="Load"/>.</summary>
    public void Save()
    {
        if (AppData.ResetDone) return; // after "Zurücksetzen" nothing may bring the data folder back
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new Dto
        {
            ApiKeyProtected = ApiKey.Length == 0 ? "" : _protector.Protect(ApiKey),
            OpenAiKeyProtected = OpenAiKey.Length == 0 ? "" : _protector.Protect(OpenAiKey),
            SpeechEngine = EngineName(SpeechEngine),
            TextEngine = EngineName(TextEngine),
            OpenAiTranscriptionModel = OpenAiTranscriptionModel,
            OpenAiPromptModel = OpenAiPromptModel,
            Glossary = Glossary,
            DictatePromptTemplate = DictatePromptTemplate,
            DictateSystemPrompt = DictateSystemPrompt,
            PromptModeSystemPrompt = PromptModeSystemPrompt,
            TranscriptionModel = TranscriptionModel,
            PromptModel = PromptModel,
            DictateHotkey = DictateHotkey,
            PromptHotkey = PromptHotkey,
            GlossaryHotkey = GlossaryHotkey,
            Microphone = Microphone,
            OfflineMode = OfflineMode,
            OfflineLlmId = OfflineLlmId,
            QuickActions = QuickActions,
            AutoPaste = AutoPaste,
            Sounds = Sounds,
            AutoUpdateCheck = AutoUpdateCheck,
            RestoreClipboard = RestoreClipboard,
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class Dto
    {
        [JsonPropertyName("apiKeyProtected")] public string? ApiKeyProtected { get; set; }
        [JsonPropertyName("openAiKeyProtected")] public string? OpenAiKeyProtected { get; set; }
        [JsonPropertyName("speechEngine")] public string? SpeechEngine { get; set; }
        [JsonPropertyName("textEngine")] public string? TextEngine { get; set; }
        [JsonPropertyName("openAiTranscriptionModel")] public string? OpenAiTranscriptionModel { get; set; }
        [JsonPropertyName("openAiPromptModel")] public string? OpenAiPromptModel { get; set; }
        [JsonPropertyName("glossary")] public List<string>? Glossary { get; set; }
        [JsonPropertyName("dictatePromptTemplate")] public string? DictatePromptTemplate { get; set; }
        [JsonPropertyName("promptSystemPrompt")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyPromptSystemPrompt { get; set; }
        [JsonPropertyName("dictateSystemPrompt")] public string? DictateSystemPrompt { get; set; }
        [JsonPropertyName("promptModeSystemPrompt")] public string? PromptModeSystemPrompt { get; set; }
        [JsonPropertyName("transcriptionModel")] public string? TranscriptionModel { get; set; }
        [JsonPropertyName("promptModel")] public string? PromptModel { get; set; }
        [JsonPropertyName("dictateHotkey")] public string? DictateHotkey { get; set; }
        [JsonPropertyName("promptHotkey")] public string? PromptHotkey { get; set; }
        [JsonPropertyName("glossaryHotkey")] public string? GlossaryHotkey { get; set; }
        [JsonPropertyName("microphone")] public string? Microphone { get; set; }
        [JsonPropertyName("offlineMode")] public bool? OfflineMode { get; set; }
        [JsonPropertyName("offlineLlmId")] public string? OfflineLlmId { get; set; }
        [JsonPropertyName("quickActions")] public List<string>? QuickActions { get; set; }
        [JsonPropertyName("autoPaste")] public bool? AutoPaste { get; set; }
        [JsonPropertyName("sounds")] public bool? Sounds { get; set; }
        [JsonPropertyName("autoUpdateCheck")] public bool? AutoUpdateCheck { get; set; }
        [JsonPropertyName("restoreClipboard")] public bool? RestoreClipboard { get; set; }
    }
}
