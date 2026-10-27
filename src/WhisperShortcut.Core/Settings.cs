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
        ActiveProfileId = Profiles[0].Id;
    }

    /// <summary>Default location: %APPDATA%\WhisperShortcut\settings.json.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperShortcut", "settings.json");

    /// <summary>Gemini API key (plain in memory, protected on disk). Read at call time by GeminiClient.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Vocabulary terms that bias transcription spelling.</summary>
    public List<string> Glossary { get; set; } = new();

    /// <summary>
    /// The template ("Vorlage") for new texts in Dictate Prompt; appended to the editing prompt and applied only
    /// when nothing is selected. Empty = no template. JSON name kept from the former "system prompt" field.
    /// </summary>
    public string PromptSystemPrompt
    {
        get => ActiveProfile.Template;
        set => ActiveProfile.Template = value;
    }

    /// <summary>
    /// The user's own transcription instruction for Dictate (Gemini only; offline speech recognition takes no prompt).
    /// Empty = Prompts.DefaultTranscriptionSystemPrompt.
    /// </summary>
    public string DictateSystemPrompt { get; set; } = "";

    /// <summary>
    /// The user's own editing instruction for Dictate Prompt (online and offline).
    /// Empty = Prompts.DefaultPromptModeSystemPrompt. The template for new texts (PromptSystemPrompt) is appended to it.
    /// </summary>
    public string PromptModeSystemPrompt
    {
        get => ActiveProfile.Instructions;
        set => ActiveProfile.Instructions = value;
    }

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
    public List<string> QuickActions
    {
        get => ActiveProfile.QuickActions;
        set => ActiveProfile.QuickActions = value;
    }

    /// <summary>All saved prompt profiles ("Prompt-Vorlagen"). Never empty after <see cref="Load"/>.</summary>
    public List<PromptProfile> Profiles { get; set; } = new() { NewStandardProfile() };

    /// <summary>Id of the profile whose instructions, template and quick actions are in use.</summary>
    public string ActiveProfileId { get; set; } = "";

    /// <summary>The profile with <see cref="ActiveProfileId"/>; the first one if the id is unknown.</summary>
    public PromptProfile ActiveProfile
    {
        get
        {
            if (Profiles.Count == 0) Profiles.Add(NewStandardProfile());
            return Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];
        }
    }

    private static PromptProfile NewStandardProfile() => new() { Id = PromptProfiles.NewId(), Name = PromptProfiles.DefaultName };

    /// <summary>Paste the result at the cursor with Ctrl+V. Off = the text only goes to the clipboard.</summary>
    public bool AutoPaste { get; set; } = true;

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
            s.Glossary = dto.Glossary ?? new();
            s.DictateSystemPrompt = CustomOrEmpty(dto.DictateSystemPrompt, Prompts.DefaultTranscriptionSystemPrompt);
            s.TranscriptionModel = GeminiModels.NormalizeTranscription(dto.TranscriptionModel);
            s.PromptModel = GeminiModels.NormalizePrompt(dto.PromptModel);
            if (!string.IsNullOrWhiteSpace(dto.DictateHotkey)) s.DictateHotkey = dto.DictateHotkey;
            if (!string.IsNullOrWhiteSpace(dto.PromptHotkey)) s.PromptHotkey = dto.PromptHotkey;
            if (!string.IsNullOrWhiteSpace(dto.GlossaryHotkey)) s.GlossaryHotkey = dto.GlossaryHotkey;
            s.Microphone = dto.Microphone ?? "";
            s.OfflineMode = dto.OfflineMode ?? false;
            s.OfflineLlmId = dto.OfflineLlmId ?? "";
            s.AutoPaste = dto.AutoPaste ?? true;
            s.RestoreClipboard = dto.RestoreClipboard ?? true;
            LoadProfiles(s, dto);
        }
        catch (Exception ex)
        {
            Log.Error("settings.json unreadable, using defaults", ex);
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        }
        return s;
    }

    /// <summary>
    /// Builds <see cref="Profiles"/> from the file. A file without profiles (written before they existed, or an empty
    /// list) becomes one "Standard" profile made from the old top-level fields.
    /// </summary>
    private static void LoadProfiles(Settings s, Dto dto)
    {
        var profiles = new List<PromptProfile>();
        foreach (var p in dto.Profiles ?? new())
        {
            if (p == null) continue;
            var id = string.IsNullOrWhiteSpace(p.Id) || profiles.Any(x => x.Id == p.Id) ? PromptProfiles.NewId() : p.Id;
            profiles.Add(NormalizedProfile(id, PromptProfiles.UniqueName(profiles.Select(x => x.Name), p.Name ?? ""),
                p.Instructions, p.Template, p.QuickActions));
        }
        if (profiles.Count == 0)
        {
            profiles.Add(NormalizedProfile(PromptProfiles.NewId(), PromptProfiles.DefaultName,
                dto.PromptModeSystemPrompt, dto.PromptSystemPrompt, dto.QuickActions));
            s.ActiveProfileId = profiles[0].Id;
        }
        else
        {
            s.ActiveProfileId = profiles.Any(x => x.Id == dto.ActiveProfileId) ? dto.ActiveProfileId! : profiles[0].Id;
        }
        s.Profiles = profiles;
    }

    private static PromptProfile NormalizedProfile(string id, string name, string? instructions, string? template, List<string>? quickActions) => new()
    {
        Id = id,
        Name = name,
        Instructions = CustomOrEmpty(instructions, Prompts.DefaultPromptModeSystemPrompt),
        Template = template ?? "",
        // Missing field = old file, give the defaults; an explicit empty list means the user removed them all.
        QuickActions = quickActions == null || QuickActionList.IsLegacyDefaults(quickActions)
            ? new(QuickActionList.Defaults)
            : QuickActionList.Clean(quickActions),
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
            Glossary = Glossary,
            // The three old top-level fields mirror the active profile, so a downgrade to a build that
            // knows no profiles still finds the prompts the user is working with.
            PromptSystemPrompt = PromptSystemPrompt,
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
            Profiles = Profiles.Select(p => new ProfileDto
            {
                Id = p.Id, Name = p.Name, Instructions = p.Instructions, Template = p.Template, QuickActions = p.QuickActions,
            }).ToList(),
            ActiveProfileId = ActiveProfile.Id,
            AutoPaste = AutoPaste,
            RestoreClipboard = RestoreClipboard,
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
        [JsonPropertyName("profiles")] public List<ProfileDto>? Profiles { get; set; }
        [JsonPropertyName("activeProfileId")] public string? ActiveProfileId { get; set; }
        [JsonPropertyName("autoPaste")] public bool? AutoPaste { get; set; }
        [JsonPropertyName("restoreClipboard")] public bool? RestoreClipboard { get; set; }
    }

    private sealed class ProfileDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("instructions")] public string? Instructions { get; set; }
        [JsonPropertyName("template")] public string? Template { get; set; }
        [JsonPropertyName("quickActions")] public List<string>? QuickActions { get; set; }
    }
}
