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

    /// <summary>Vocabulary terms that bias transcription spelling.</summary>
    public List<string> Glossary { get; set; } = new();

    /// <summary>The user's own Dictate Prompt system prompt (quote template). Empty = default prompt.</summary>
    public string PromptSystemPrompt { get; set; } = "";

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
    public string GlossaryHotkey { get; set; } = "Ctrl+Shift+G";

    /// <summary>Offline mode: dictation and Dictate Prompt run on this PC (Parakeet + local LLM).</summary>
    public bool OfflineMode { get; set; }

    /// <summary>Id of the chosen local LLM (see <see cref="OfflineCatalog"/>); empty = recommended for this PC.</summary>
    public string OfflineLlmId { get; set; } = "";

    /// <summary>Quick instructions offered while recording (see <see cref="QuickActionList"/>).</summary>
    public List<string> QuickActions { get; set; } = new(QuickActionList.Defaults);

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
            s.Glossary = dto.Glossary ?? new();
            s.PromptSystemPrompt = dto.PromptSystemPrompt ?? "";
            if (!string.IsNullOrWhiteSpace(dto.TranscriptionModel)) s.TranscriptionModel = dto.TranscriptionModel;
            if (!string.IsNullOrWhiteSpace(dto.PromptModel)) s.PromptModel = dto.PromptModel;
            if (!string.IsNullOrWhiteSpace(dto.DictateHotkey)) s.DictateHotkey = dto.DictateHotkey;
            if (!string.IsNullOrWhiteSpace(dto.PromptHotkey)) s.PromptHotkey = dto.PromptHotkey;
            if (!string.IsNullOrWhiteSpace(dto.GlossaryHotkey)) s.GlossaryHotkey = dto.GlossaryHotkey;
            s.Microphone = dto.Microphone ?? "";
            s.OfflineMode = dto.OfflineMode ?? false;
            s.OfflineLlmId = dto.OfflineLlmId ?? "";
            // Missing field = old file, give the defaults; an explicit empty list means the user removed them all.
            s.QuickActions = dto.QuickActions == null
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

    /// <summary>Writes the settings to the path given at <see cref="Load"/>.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new Dto
        {
            ApiKeyProtected = ApiKey.Length == 0 ? "" : _protector.Protect(ApiKey),
            Glossary = Glossary,
            PromptSystemPrompt = PromptSystemPrompt,
            TranscriptionModel = TranscriptionModel,
            PromptModel = PromptModel,
            DictateHotkey = DictateHotkey,
            PromptHotkey = PromptHotkey,
            GlossaryHotkey = GlossaryHotkey,
            Microphone = Microphone,
            OfflineMode = OfflineMode,
            OfflineLlmId = OfflineLlmId,
            QuickActions = QuickActions,
        };
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class Dto
    {
        [JsonPropertyName("apiKeyProtected")] public string? ApiKeyProtected { get; set; }
        [JsonPropertyName("glossary")] public List<string>? Glossary { get; set; }
        [JsonPropertyName("promptSystemPrompt")] public string? PromptSystemPrompt { get; set; }
        [JsonPropertyName("transcriptionModel")] public string? TranscriptionModel { get; set; }
        [JsonPropertyName("promptModel")] public string? PromptModel { get; set; }
        [JsonPropertyName("dictateHotkey")] public string? DictateHotkey { get; set; }
        [JsonPropertyName("promptHotkey")] public string? PromptHotkey { get; set; }
        [JsonPropertyName("glossaryHotkey")] public string? GlossaryHotkey { get; set; }
        [JsonPropertyName("microphone")] public string? Microphone { get; set; }
        [JsonPropertyName("offlineMode")] public bool? OfflineMode { get; set; }
        [JsonPropertyName("offlineLlmId")] public string? OfflineLlmId { get; set; }
        [JsonPropertyName("quickActions")] public List<string>? QuickActions { get; set; }
    }
}
