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

    /// <summary>
    /// "Only when spoken": offline dictation writes punctuation only where it is spoken ("Punkt", "Komma").
    /// Use <see cref="PunctuationModes.Effective"/> to decide; cloud models and meetings ignore it.
    /// </summary>
    public PunctuationMode PunctuationMode { get; set; } = PunctuationMode.Automatic;

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

    // Shortcut defaults follow the Mac (ShortcutConfig.default): the same digit for the same function, with Alt as the
    // Windows modifier (Ctrl+Alt is AltGr on German keyboards). Alt+4 is Read Aloud (Vorlesen).
    // Installs that ran before keep LegacyShortcuts: see the pin in Load.

    /// <summary>New-install hotkey for plain dictation (Mac ⌃⌥1).</summary>
    public const string DefaultDictateHotkey = "Alt+1";

    /// <summary>New-install hotkey for Dictate Prompt (Mac ⌃⌥2).</summary>
    public const string DefaultPromptHotkey = "Alt+2";

    /// <summary>New-install hotkey for Screenshot (Mac ⌃⌥3).</summary>
    public const string DefaultScreenshotHotkey = "Alt+3";

    /// <summary>New-install hotkey for Read Aloud (Mac ⌃⌥4).</summary>
    public const string DefaultReadAloudHotkey = "Alt+4";

    /// <summary>Suggested key for Live diktieren (Mac ⌃⌥5); ships disabled.</summary>
    public const string DefaultLiveDictateHotkey = "Alt+5";

    /// <summary>Suggested key for Markierung ins Glossar (Mac ⌃⌥7); ships disabled.</summary>
    public const string DefaultGlossaryHotkey = "Alt+7";

    /// <summary>New-install hotkey that opens the settings window (Mac ⌃⌥0).</summary>
    public const string DefaultOpenSettingsHotkey = "Alt+0";

    /// <summary>Hotkeys every install before the Mac-digit defaults was running on (Mac: legacyCommandDigit).</summary>
    public static class LegacyShortcuts
    {
        /// <summary>Dictate before the switch.</summary>
        public const string Dictate = "Ctrl+Shift+D";

        /// <summary>Dictate Prompt before the switch.</summary>
        public const string Prompt = "Ctrl+Shift+P";

        /// <summary>Glossary before the switch (on).</summary>
        public const string Glossary = "Ctrl+Shift+K";

        /// <summary>Screenshot before the switch: unassigned.</summary>
        public const string Screenshot = "";

        /// <summary>Live diktieren before the switch: unassigned.</summary>
        public const string LiveDictate = "";

        /// <summary>Open settings did not exist: unassigned.</summary>
        public const string OpenSettings = "";

        /// <summary>Read Aloud did not exist: unassigned.</summary>
        public const string ReadAloud = "";
    }

    /// <summary>Hotkey for plain dictation.</summary>
    public string DictateHotkey { get; set; } = DefaultDictateHotkey;

    /// <summary>Dictate hotkey on (Mac isEnabled). Use <see cref="ActiveDictateHotkey"/> to register.</summary>
    public bool DictateHotkeyEnabled { get; set; } = true;

    /// <summary>Hotkey for Dictate Prompt.</summary>
    public string PromptHotkey { get; set; } = DefaultPromptHotkey;

    /// <summary>Dictate Prompt hotkey on. Use <see cref="ActivePromptHotkey"/> to register.</summary>
    public bool PromptHotkeyEnabled { get; set; } = true;

    /// <summary>Hotkey that appends the selected text to the Glossary. With the flag off it is only the suggestion.</summary>
    public string GlossaryHotkey { get; set; } = DefaultGlossaryHotkey;

    /// <summary>Glossary hotkey on. Off for new installs, like the Mac. Use <see cref="ActiveGlossaryHotkey"/> to register.</summary>
    public bool GlossaryHotkeyEnabled { get; set; }

    /// <summary>Hotkey that opens the settings window; "" = unassigned.</summary>
    public string OpenSettingsHotkey { get; set; } = DefaultOpenSettingsHotkey;

    /// <summary>Open-settings hotkey on. Use <see cref="ActiveOpenSettingsHotkey"/> to register.</summary>
    public bool OpenSettingsHotkeyEnabled { get; set; } = true;

    /// <summary>Hotkey that reads the selected text aloud; "" = unassigned.</summary>
    public string ReadAloudHotkey { get; set; } = DefaultReadAloudHotkey;

    /// <summary>Read Aloud hotkey on. Use <see cref="ActiveReadAloudHotkey"/> to register.</summary>
    public bool ReadAloudHotkeyEnabled { get; set; } = true;

    /// <summary>Read Aloud model id (see <see cref="Core.ReadAloud.Models"/>); unknown ids fall back to the default on load.</summary>
    public string ReadAloudModel { get; set; } = ReadAloud.DefaultModel;

    /// <summary>Chosen Gemini voice for Read Aloud (a <see cref="Core.ReadAloud.Voices"/> id).</summary>
    public string ReadAloudVoiceGemini { get; set; } = ReadAloud.DefaultVoice(TtsProvider.Gemini);

    /// <summary>Chosen OpenAI voice for Read Aloud (a <see cref="Core.ReadAloud.Voices"/> id).</summary>
    public string ReadAloudVoiceOpenAi { get; set; } = ReadAloud.DefaultVoice(TtsProvider.OpenAI);

    /// <summary>Read Aloud playback speed, one of <see cref="Core.ReadAloud.Speeds"/>.</summary>
    public double ReadAloudSpeed { get; set; } = ReadAloud.DefaultSpeed;

    /// <summary>Let the model rewrite code, logs and Markdown into something speakable before reading (Mac: Smart Rewrite).</summary>
    public bool ReadAloudSmartRewrite { get; set; } = true;

    /// <summary>The selected Read Aloud model.</summary>
    [JsonIgnore]
    public TtsModel ReadAloudTtsModel => ReadAloud.ModelById(ReadAloudModel);

    /// <summary>The chosen Read Aloud voice of <paramref name="provider"/>.</summary>
    public string ReadAloudVoiceFor(TtsProvider provider) =>
        provider == TtsProvider.Gemini
            ? ReadAloud.NormalizeVoice(provider, ReadAloudVoiceGemini)
            : ReadAloud.NormalizeVoice(provider, ReadAloudVoiceOpenAi);

    /// <summary>
    /// The right Ctrl key alone toggles dictation, the Windows stand-in for the Mac's Fn key. On by default like the Mac.
    /// The keyboard hook that reads it comes separately.
    /// </summary>
    public bool RightCtrlDictation { get; set; } = true;

    /// <summary>The stored key when its flag is on, otherwise "" (nothing to register).</summary>
    public static string Active(string hotkey, bool enabled) => enabled ? hotkey : "";

    /// <summary>Dictate hotkey to register, "" when off.</summary>
    public string ActiveDictateHotkey => Active(DictateHotkey, DictateHotkeyEnabled);

    /// <summary>Dictate Prompt hotkey to register, "" when off.</summary>
    public string ActivePromptHotkey => Active(PromptHotkey, PromptHotkeyEnabled);

    /// <summary>Glossary hotkey to register, "" when off.</summary>
    public string ActiveGlossaryHotkey => Active(GlossaryHotkey, GlossaryHotkeyEnabled);

    /// <summary>Screenshot hotkey to register, "" when off or unassigned.</summary>
    public string ActiveScreenshotHotkey => Active(ScreenshotHotkey, ScreenshotHotkeyEnabled);

    /// <summary>Live diktieren hotkey to register, "" when off or unassigned.</summary>
    public string ActiveLiveDictateHotkey => Active(LiveDictateHotkey, LiveDictateHotkeyEnabled);

    /// <summary>Open-settings hotkey to register, "" when off or unassigned.</summary>
    public string ActiveOpenSettingsHotkey => Active(OpenSettingsHotkey, OpenSettingsHotkeyEnabled);

    /// <summary>Read Aloud hotkey to register, "" when off or unassigned.</summary>
    public string ActiveReadAloudHotkey => Active(ReadAloudHotkey, ReadAloudHotkeyEnabled);

    /// <summary>Dictation templates ("Vorlagen"), in the order the user arranged them.</summary>
    public List<DictationTemplate> Templates { get; set; } = new();

    /// <summary>Generic hotkey that applies the last-used template; "" = unassigned.</summary>
    public string TemplateHotkey { get; set; } = "";

    /// <summary>
    /// Send a screenshot taken with the Screenshot hotkey along with the next Dictate Prompt or template request.
    /// On for new installs, like the Mac (screenshot works out of the box there). Installs from before the Mac
    /// defaults keep it off when the field is missing: screens of practice software show patient data.
    /// </summary>
    public bool SendScreenshots { get; set; } = true;

    /// <summary>Hotkey that opens the Windows snip UI; "" = unassigned.</summary>
    public string ScreenshotHotkey { get; set; } = DefaultScreenshotHotkey;

    /// <summary>Screenshot hotkey on. Use <see cref="ActiveScreenshotHotkey"/> to register.</summary>
    public bool ScreenshotHotkeyEnabled { get; set; } = true;

    /// <summary>Id of the template the generic hotkey applied last; "" = none yet.</summary>
    public string LastTemplateId { get; set; } = "";

    /// <summary>Hotkey that starts/stops a Live Meeting. Empty = no hotkey (unassigned by default).</summary>
    public string MeetingHotkey { get; set; } = "";

    /// <summary>Live diktieren (macOS Dictate Live). "" = unassigned. New installs get the suggestion Alt+5, switched off like the Mac.</summary>
    public string LiveDictateHotkey { get; set; } = DefaultLiveDictateHotkey;

    /// <summary>Live diktieren hotkey on. Off for new installs. Use <see cref="ActiveLiveDictateHotkey"/> to register.</summary>
    public bool LiveDictateHotkeyEnabled { get; set; }

    /// <summary>Which audio a Live Meeting records: "auto", "mic" or "mix" (see <see cref="MeetingAudioRouting"/>).</summary>
    public string MeetingAudioMode { get; set; } = "auto";

    /// <summary>Write a summary when a Live Meeting ends.</summary>
    public bool MeetingSummary { get; set; } = true;

    /// <summary>Offline mode: dictation and Dictate Prompt run on this PC (Parakeet + local LLM).</summary>
    public bool OfflineMode { get; set; }

    /// <summary>Id of the chosen local LLM (see <see cref="OfflineCatalog"/>); empty = recommended for this PC.</summary>
    public string OfflineLlmId { get; set; } = "";

    /// <summary>Quick instructions offered while recording (see <see cref="QuickActionList"/>).</summary>
    public List<string> QuickActions { get; set; } = new(QuickActionList.Defaults);

    /// <summary>Paste the result at the cursor with Ctrl+V. Off = the text only goes to the clipboard.</summary>
    public bool AutoPaste { get; set; } = true;

    /// <summary>Play the short start/stop/error tones. Off for new installs (the Mac has none); older files without the field keep them.</summary>
    public bool Sounds { get; set; }

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
        // No file = fresh install: the Mac-digit defaults above. Save writes the pin flag with them.
        if (!File.Exists(path)) return s;
        // A file = an install that ran before. Until its file carries the pin flag, missing shortcut values mean the
        // old defaults, not the new ones (Mac: the pin in ShortcutConfigManager.loadConfiguration), so an update never
        // moves the user's shortcuts. Also for an unreadable file: it existed, so this PC ran an earlier version.
        s.PinLegacyShortcuts();
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), Json);
            if (dto == null) return s;
            if (dto.ShortcutDefaultsAltV1 == true) s.ResetShortcutsToDefaults();
            var rewrite = !string.IsNullOrEmpty(dto.ApiKeyPlain) || !string.IsNullOrEmpty(dto.OpenAiKeyPlain);
            if (!string.IsNullOrEmpty(dto.ApiKeyProtected))
            {
                try { s.ApiKey = protector.Unprotect(dto.ApiKeyProtected) ?? ""; }
                catch (Exception) { s.ApiKey = ""; }
            }
            else if (!string.IsNullOrEmpty(dto.ApiKeyPlain))
            {
                s.ApiKey = dto.ApiKeyPlain;
            }
            if (!string.IsNullOrEmpty(dto.OpenAiKeyProtected))
            {
                try { s.OpenAiKey = protector.Unprotect(dto.OpenAiKeyProtected) ?? ""; }
                catch (Exception) { s.OpenAiKey = ""; }
            }
            else if (!string.IsNullOrEmpty(dto.OpenAiKeyPlain))
            {
                s.OpenAiKey = dto.OpenAiKeyPlain;
            }
            s.SpeechEngine = ParseEngine(dto.SpeechEngine);
            s.TextEngine = ParseEngine(dto.TextEngine);
            s.PunctuationMode = ParsePunctuationMode(dto.PunctuationMode);
            s.OpenAiTranscriptionModel = OpenAiModels.NormalizeTranscription(dto.OpenAiTranscriptionModel);
            s.OpenAiPromptModel = OpenAiModels.NormalizePrompt(dto.OpenAiPromptModel);
            // JSON null entries (hand-edited or a half-written file) must not stay in the list:
            // GlossaryEdit.TryAdd would then NullRef when the user adds a term.
            s.Glossary = (dto.Glossary ?? new()).Where(t => t != null).ToList()!;
            s.DictatePromptTemplate = dto.DictatePromptTemplate ?? dto.LegacyPromptSystemPrompt ?? "";
            s.DictateSystemPrompt = CustomOrEmpty(dto.DictateSystemPrompt, Prompts.DefaultTranscriptionSystemPrompt);
            s.PromptModeSystemPrompt = CustomOrEmpty(dto.PromptModeSystemPrompt, Prompts.DefaultPromptModeSystemPrompt);
            s.TranscriptionModel = GeminiModels.NormalizeTranscription(dto.TranscriptionModel);
            s.PromptModel = GeminiModels.NormalizePrompt(dto.PromptModel);
            if (!string.IsNullOrWhiteSpace(dto.DictateHotkey)) s.DictateHotkey = dto.DictateHotkey;
            if (!string.IsNullOrWhiteSpace(dto.PromptHotkey)) s.PromptHotkey = dto.PromptHotkey;
            if (!string.IsNullOrWhiteSpace(dto.GlossaryHotkey)) s.GlossaryHotkey = dto.GlossaryHotkey;
            // "" is a stored choice (unassigned) for these; only a missing field falls back.
            if (dto.OpenSettingsHotkey != null) s.OpenSettingsHotkey = dto.OpenSettingsHotkey;
            s.DictateHotkeyEnabled = dto.DictateHotkeyEnabled ?? s.DictateHotkeyEnabled;
            s.PromptHotkeyEnabled = dto.PromptHotkeyEnabled ?? s.PromptHotkeyEnabled;
            s.GlossaryHotkeyEnabled = dto.GlossaryHotkeyEnabled ?? s.GlossaryHotkeyEnabled;
            s.ScreenshotHotkeyEnabled = dto.ScreenshotHotkeyEnabled ?? s.ScreenshotHotkeyEnabled;
            s.LiveDictateHotkeyEnabled = dto.LiveDictateHotkeyEnabled ?? s.LiveDictateHotkeyEnabled;
            s.OpenSettingsHotkeyEnabled = dto.OpenSettingsHotkeyEnabled ?? s.OpenSettingsHotkeyEnabled;
            if (dto.ReadAloudHotkey != null) s.ReadAloudHotkey = dto.ReadAloudHotkey;
            s.ReadAloudHotkeyEnabled = dto.ReadAloudHotkeyEnabled ?? s.ReadAloudHotkeyEnabled;
            s.ReadAloudModel = ReadAloud.NormalizeModel(dto.ReadAloudModel);
            s.ReadAloudVoiceGemini = ReadAloud.NormalizeVoice(TtsProvider.Gemini, dto.ReadAloudVoiceGemini);
            s.ReadAloudVoiceOpenAi = ReadAloud.NormalizeVoice(TtsProvider.OpenAI, dto.ReadAloudVoiceOpenAi);
            s.ReadAloudSpeed = ReadAloud.NormalizeSpeed(dto.ReadAloudSpeed ?? ReadAloud.DefaultSpeed);
            s.ReadAloudSmartRewrite = dto.ReadAloudSmartRewrite ?? true;
            s.RightCtrlDictation = dto.RightCtrlDictation ?? true;
            s.Templates = LoadTemplates(dto.Templates);
            s.TemplateHotkey = dto.TemplateHotkey ?? "";
            s.SendScreenshots = dto.SendScreenshots ?? false;
            if (dto.ScreenshotHotkey != null) s.ScreenshotHotkey = dto.ScreenshotHotkey;
            s.LastTemplateId = dto.LastTemplateId ?? "";
            s.Microphone = dto.Microphone ?? "";
            s.MeetingHotkey = dto.MeetingHotkey ?? "";
            if (dto.LiveDictateHotkey != null) s.LiveDictateHotkey = dto.LiveDictateHotkey;
            s.MeetingAudioMode = MeetingAudioRouting.Name(MeetingAudioRouting.Parse(dto.MeetingAudioMode));
            s.MeetingSummary = dto.MeetingSummary ?? true;
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
            // An update adds the Read Aloud key to a file that never had it. It must not take a key the user already uses.
            if (dto.ReadAloudHotkey == null && s.ReadAloudHotkeyEnabled && s.ReadAloudKeyClashes())
                s.ReadAloudHotkeyEnabled = false;
            // A leftover plaintext key field is imported above and must leave the file on the next write.
            if (rewrite)
            {
                try { s.Save(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings.json unreadable, using defaults", ex);
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        }
        return s;
    }

    /// <summary>The shortcuts an install had before the Mac-digit defaults; every flag on, as it behaved then.</summary>
    private void PinLegacyShortcuts()
    {
        DictateHotkey = LegacyShortcuts.Dictate;
        PromptHotkey = LegacyShortcuts.Prompt;
        GlossaryHotkey = LegacyShortcuts.Glossary;
        ScreenshotHotkey = LegacyShortcuts.Screenshot;
        LiveDictateHotkey = LegacyShortcuts.LiveDictate;
        OpenSettingsHotkey = LegacyShortcuts.OpenSettings;
        ReadAloudHotkey = LegacyShortcuts.ReadAloud;
        DictateHotkeyEnabled = PromptHotkeyEnabled = GlossaryHotkeyEnabled = true;
        ScreenshotHotkeyEnabled = LiveDictateHotkeyEnabled = OpenSettingsHotkeyEnabled = true;
        ReadAloudHotkeyEnabled = true;
    }

    // True when the Read Aloud key equals any other stored key (app keys and template keys); empty keys never clash.
    private bool ReadAloudKeyClashes()
    {
        if (string.IsNullOrWhiteSpace(ReadAloudHotkey)) return false;
        var others = new List<string>
        {
            DictateHotkey, PromptHotkey, GlossaryHotkey, ScreenshotHotkey, LiveDictateHotkey, OpenSettingsHotkey,
            MeetingHotkey, TemplateHotkey,
        };
        others.AddRange(Templates.Select(t => t.Hotkey));
        return others.Any(k => !string.IsNullOrWhiteSpace(k)
            && string.Equals(k.Trim(), ReadAloudHotkey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The new-install shortcuts (same values as the property initializers).</summary>
    private void ResetShortcutsToDefaults()
    {
        var d = new Settings(_path, _protector);
        DictateHotkey = d.DictateHotkey;
        PromptHotkey = d.PromptHotkey;
        GlossaryHotkey = d.GlossaryHotkey;
        ScreenshotHotkey = d.ScreenshotHotkey;
        LiveDictateHotkey = d.LiveDictateHotkey;
        OpenSettingsHotkey = d.OpenSettingsHotkey;
        ReadAloudHotkey = d.ReadAloudHotkey;
        DictateHotkeyEnabled = d.DictateHotkeyEnabled;
        PromptHotkeyEnabled = d.PromptHotkeyEnabled;
        GlossaryHotkeyEnabled = d.GlossaryHotkeyEnabled;
        ScreenshotHotkeyEnabled = d.ScreenshotHotkeyEnabled;
        LiveDictateHotkeyEnabled = d.LiveDictateHotkeyEnabled;
        OpenSettingsHotkeyEnabled = d.OpenSettingsHotkeyEnabled;
        ReadAloudHotkeyEnabled = d.ReadAloudHotkeyEnabled;
    }

    // Missing list = old file: no templates. Null entries are skipped; a blank id gets a fresh one.
    private static List<DictationTemplate> LoadTemplates(List<TemplateDto?>? dtos)
    {
        var list = new List<DictationTemplate>();
        if (dtos == null) return list;
        foreach (var d in dtos)
        {
            if (d == null) continue;
            list.Add(new DictationTemplate
            {
                Id = string.IsNullOrWhiteSpace(d.Id) ? DictationTemplate.NewId() : d.Id,
                Name = d.Name ?? "",
                Instructions = (d.Instructions ?? "").Replace("\r\n", "\n").Replace('\r', '\n'),
                Hotkey = d.Hotkey ?? "",
            });
        }
        return list;
    }

    // Unknown or missing = Gemini, the engine every old settings file used.
    private static Engine ParseEngine(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "local" => Engine.Local,
        "openai" => Engine.OpenAI,
        _ => Engine.Gemini,
    };

    // Unknown or missing = Automatic.
    private static PunctuationMode ParsePunctuationMode(string? v) =>
        v?.Trim().ToLowerInvariant() == "spokenonly" ? PunctuationMode.SpokenOnly : PunctuationMode.Automatic;

    private static string PunctuationModeName(PunctuationMode m) =>
        m == PunctuationMode.SpokenOnly ? "spokenOnly" : "automatic";

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
        try
        {
            SaveCore();
        }
        catch (IOException ex) when (DiskSpace.IsFull(ex))
        {
            throw new DiskFullException(DiskSpace.SettingsMessage, ex);
        }
    }

    private void SaveCore()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = new Dto
        {
            ApiKeyProtected = ApiKey.Length == 0 ? "" : _protector.Protect(ApiKey),
            OpenAiKeyProtected = OpenAiKey.Length == 0 ? "" : _protector.Protect(OpenAiKey),
            SpeechEngine = EngineName(SpeechEngine),
            TextEngine = EngineName(TextEngine),
            PunctuationMode = PunctuationModeName(PunctuationMode),
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
            OpenSettingsHotkey = OpenSettingsHotkey,
            DictateHotkeyEnabled = DictateHotkeyEnabled,
            PromptHotkeyEnabled = PromptHotkeyEnabled,
            GlossaryHotkeyEnabled = GlossaryHotkeyEnabled,
            ScreenshotHotkeyEnabled = ScreenshotHotkeyEnabled,
            LiveDictateHotkeyEnabled = LiveDictateHotkeyEnabled,
            OpenSettingsHotkeyEnabled = OpenSettingsHotkeyEnabled,
            ReadAloudHotkey = ReadAloudHotkey,
            ReadAloudHotkeyEnabled = ReadAloudHotkeyEnabled,
            ReadAloudModel = ReadAloudModel,
            ReadAloudVoiceGemini = ReadAloudVoiceGemini,
            ReadAloudVoiceOpenAi = ReadAloudVoiceOpenAi,
            ReadAloudSpeed = ReadAloudSpeed,
            ReadAloudSmartRewrite = ReadAloudSmartRewrite,
            RightCtrlDictation = RightCtrlDictation,
            // Every value above is written out, so from now on a missing field means the new default.
            ShortcutDefaultsAltV1 = true,
            Templates = Templates.Select(t => (TemplateDto?)new TemplateDto
            {
                Id = t.Id, Name = t.Name, Instructions = t.Instructions, Hotkey = t.Hotkey,
            }).ToList(),
            TemplateHotkey = TemplateHotkey,
            SendScreenshots = SendScreenshots,
            ScreenshotHotkey = ScreenshotHotkey,
            LastTemplateId = LastTemplateId,
            Microphone = Microphone,
            MeetingHotkey = MeetingHotkey,
            LiveDictateHotkey = LiveDictateHotkey,
            MeetingAudioMode = MeetingAudioMode,
            MeetingSummary = MeetingSummary,
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

    private sealed class TemplateDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("instructions")] public string? Instructions { get; set; }
        [JsonPropertyName("hotkey")] public string? Hotkey { get; set; }
    }

    private sealed class Dto
    {
        [JsonPropertyName("apiKeyProtected")] public string? ApiKeyProtected { get; set; }
        [JsonPropertyName("openAiKeyProtected")] public string? OpenAiKeyProtected { get; set; }
        // Read-only migration: a hand-edited or copied file may still hold a raw key. Never written back.
        [JsonPropertyName("apiKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ApiKeyPlain { get; set; }
        [JsonPropertyName("openAiKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OpenAiKeyPlain { get; set; }
        [JsonPropertyName("speechEngine")] public string? SpeechEngine { get; set; }
        [JsonPropertyName("textEngine")] public string? TextEngine { get; set; }
        [JsonPropertyName("punctuationMode")] public string? PunctuationMode { get; set; }
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
        [JsonPropertyName("openSettingsHotkey")] public string? OpenSettingsHotkey { get; set; }
        [JsonPropertyName("dictateHotkeyEnabled")] public bool? DictateHotkeyEnabled { get; set; }
        [JsonPropertyName("promptHotkeyEnabled")] public bool? PromptHotkeyEnabled { get; set; }
        [JsonPropertyName("glossaryHotkeyEnabled")] public bool? GlossaryHotkeyEnabled { get; set; }
        [JsonPropertyName("screenshotHotkeyEnabled")] public bool? ScreenshotHotkeyEnabled { get; set; }
        [JsonPropertyName("liveDictateHotkeyEnabled")] public bool? LiveDictateHotkeyEnabled { get; set; }
        [JsonPropertyName("openSettingsHotkeyEnabled")] public bool? OpenSettingsHotkeyEnabled { get; set; }
        [JsonPropertyName("readAloudHotkey")] public string? ReadAloudHotkey { get; set; }
        [JsonPropertyName("readAloudHotkeyEnabled")] public bool? ReadAloudHotkeyEnabled { get; set; }
        [JsonPropertyName("readAloudModel")] public string? ReadAloudModel { get; set; }
        [JsonPropertyName("readAloudVoiceGemini")] public string? ReadAloudVoiceGemini { get; set; }
        [JsonPropertyName("readAloudVoiceOpenAi")] public string? ReadAloudVoiceOpenAi { get; set; }
        [JsonPropertyName("readAloudSpeed")] public double? ReadAloudSpeed { get; set; }
        [JsonPropertyName("readAloudSmartRewrite")] public bool? ReadAloudSmartRewrite { get; set; }
        [JsonPropertyName("rightCtrlDictation")] public bool? RightCtrlDictation { get; set; }
        // One-time pin (Mac: shortcut_defaults_ctrl_option_v1). Missing in a file = written before the Alt+digit defaults.
        [JsonPropertyName("shortcutDefaultsAltV1")] public bool? ShortcutDefaultsAltV1 { get; set; }
        [JsonPropertyName("templates")] public List<TemplateDto?>? Templates { get; set; }
        [JsonPropertyName("templateHotkey")] public string? TemplateHotkey { get; set; }
        [JsonPropertyName("sendScreenshots")] public bool? SendScreenshots { get; set; }
        [JsonPropertyName("screenshotHotkey")] public string? ScreenshotHotkey { get; set; }
        [JsonPropertyName("lastTemplateId")] public string? LastTemplateId { get; set; }
        [JsonPropertyName("microphone")] public string? Microphone { get; set; }
        [JsonPropertyName("meetingHotkey")] public string? MeetingHotkey { get; set; }
        [JsonPropertyName("liveDictateHotkey")] public string? LiveDictateHotkey { get; set; }
        [JsonPropertyName("meetingAudioMode")] public string? MeetingAudioMode { get; set; }
        [JsonPropertyName("meetingSummary")] public bool? MeetingSummary { get; set; }
        [JsonPropertyName("offlineMode")] public bool? OfflineMode { get; set; }
        [JsonPropertyName("offlineLlmId")] public string? OfflineLlmId { get; set; }
        [JsonPropertyName("quickActions")] public List<string>? QuickActions { get; set; }
        [JsonPropertyName("autoPaste")] public bool? AutoPaste { get; set; }
        [JsonPropertyName("sounds")] public bool? Sounds { get; set; }
        [JsonPropertyName("autoUpdateCheck")] public bool? AutoUpdateCheck { get; set; }
        [JsonPropertyName("restoreClipboard")] public bool? RestoreClipboard { get; set; }
    }
}
