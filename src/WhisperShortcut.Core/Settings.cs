using System.Text;
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

/// <summary>The settings as <see cref="Settings.Capture"/> took them. Opaque: only <see cref="Settings.Restore"/> reads it.</summary>
public sealed class SettingsState
{
    internal SettingsState(string json, string? retainedApiKeyBlob = null, string? retainedOpenAiKeyBlob = null)
    {
        Json = json;
        RetainedApiKeyBlob = retainedApiKeyBlob;
        RetainedOpenAiKeyBlob = retainedOpenAiKeyBlob;
    }

    internal string Json { get; }

    /// <summary>Protected blob kept because it could not be decrypted; null when the key was readable or empty.</summary>
    internal string? RetainedApiKeyBlob { get; }

    /// <summary>Protected blob kept because it could not be decrypted; null when the key was readable or empty.</summary>
    internal string? RetainedOpenAiKeyBlob { get; }

    /// <summary>Never the content: it holds the API keys in the clear.</summary>
    public override string ToString() => nameof(SettingsState);
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
public sealed partial class Settings
{
    /// <summary>Default transcription model.</summary>
    public const string DefaultTranscriptionModel = "gemini-3.1-flash-lite";

    /// <summary>Default Dictate Prompt model.</summary>
    public const string DefaultPromptModel = "gemini-3.8-flash";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private string _apiKey = "";
    private string _openAiKey = "";
    // The on-disk blob when Unprotect failed. Written back unchanged until the user sets a new key.
    private string? _retainedApiKeyBlob;
    private string? _retainedOpenAiKeyBlob;

    private Settings(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        HotkeySlots.ApplyDefaults(this);
    }

    /// <summary>Default location: %APPDATA%\WhisperShortcut\settings.json.</summary>
    public static string DefaultPath => AppIdentity.Current.SettingsPath; // Dev: "WhisperShortcut Dev"

    /// <summary>Gemini API key (plain in memory, protected on disk). Read at call time by GeminiClient.</summary>
    public string ApiKey
    {
        get => _apiKey;
        set => SetKey(ref _apiKey, ref _retainedApiKeyBlob, value);
    }

    /// <summary>OpenAI API key (plain in memory, protected on disk). Read at call time by OpenAiClient.</summary>
    public string OpenAiKey
    {
        get => _openAiKey;
        set => SetKey(ref _openAiKey, ref _retainedOpenAiKeyBlob, value);
    }

    /// <summary>Provider and models of speech recognition (Dictate). Use <see cref="EffectiveSpeechEngine"/> to decide where it runs.</summary>
    public ModelSelection Dictate { get; set; } = ModelSelection.Default(ModelStep.Transcription);

    /// <summary>Provider and models of text processing (Dictate Prompt). Use <see cref="EffectiveTextEngine"/> to decide where it runs.</summary>
    public ModelSelection Prompt { get; set; } = ModelSelection.Default(ModelStep.Text);

    /// <summary>
    /// "Only when spoken": offline dictation writes punctuation only where it is spoken ("Punkt", "Komma").
    /// Use <see cref="PunctuationModes.Effective"/> to decide; cloud models and meetings ignore it.
    /// </summary>
    public PunctuationMode PunctuationMode { get; set; } = PunctuationMode.Automatic;

    /// <summary>
    /// What the Diktieren shortcut and right Ctrl start (Mac Settings → Dictate → Mode, <c>DictateMode</c>):
    /// a normal Diktat (default; also for old files without the key) or Live diktieren. The Live shortcut always starts Live.
    /// </summary>
    public DictateMode DictateMode { get; set; } = DictateMode.Dictate;

    /// <summary>
    /// Spoken language for OpenAI transcription (ISO 639-1, or "auto"). Gemini and Parakeet ignore it.
    /// See <see cref="TranscriptionLanguages"/>.
    /// </summary>
    public string TranscriptionLanguage { get; set; } = TranscriptionLanguages.Auto;

    /// <summary>Gemini transcription temperature (Diktieren → Erweitert); see <see cref="TranscriptionTuning"/>.</summary>
    public double TranscriptionTemperature { get; set; } = TranscriptionTuning.DefaultTemperature;

    /// <summary>Gemini transcription thinking level: minimal, low, medium or high; see <see cref="TranscriptionTuning"/>.</summary>
    public string TranscriptionThinking { get; set; } = TranscriptionTuning.DefaultThinking;

    /// <summary>
    /// The speech engine actually used. <see cref="OfflineMode"/> is the tray's "nothing leaves this PC"
    /// switch and overrides the choice with <see cref="Engine.Local"/>. Not persisted.
    /// </summary>
    [JsonIgnore]
    public Engine EffectiveSpeechEngine => Dictate.Effective(OfflineMode).Engine;

    /// <summary>
    /// The text engine actually used. <see cref="OfflineMode"/> is the tray's "nothing leaves this PC"
    /// switch and overrides the choice with <see cref="Engine.Local"/>. Not persisted.
    /// </summary>
    [JsonIgnore]
    public Engine EffectiveTextEngine => Prompt.Effective(OfflineMode).Engine;

    /// <summary>
    /// The provider whose API key <paramref name="engine"/> needs but lacks: "Gemini", "OpenAI", or null when the key is there
    /// or the engine runs on this PC (needs none). The one place that knows which key belongs to which engine.
    /// </summary>
    public string? MissingKeyProvider(Engine engine) =>
        CloudProviders.For(engine) is { } p && !p.HasKey(this) ? p.Name : null;

    private volatile List<string> _glossary = new();

    /// <summary>
    /// Vocabulary terms that bias transcription spelling. Copy-on-write: a published list is never mutated, every
    /// change builds a new list and swaps the whole value (<see cref="TryAddGlossaryTerm"/>, or assign a new list),
    /// so the meeting worker can copy it off the UI thread while the glossary hotkey adds an entry.
    /// </summary>
    public List<string> Glossary { get => _glossary; set => _glossary = value; }

    /// <summary>Appends the cleaned selection to a copy of the glossary and swaps it in only when it was added.</summary>
    public GlossaryAddResult TryAddGlossaryTerm(string? selection, out string term)
    {
        var next = _glossary.ToList();
        var result = GlossaryEdit.TryAdd(next, selection, out term);
        if (result == GlossaryAddResult.Added) _glossary = next;
        return result;
    }

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

    /// <summary>Product name of the microphone to record from; "" = the Windows default device.</summary>
    public string Microphone { get; set; } = "";

    // Key defaults, legacy keys and the on switches live in HotkeySlots (one table); the constructor applies the new-install
    // values, Load pins the legacy ones for a file that predates the Mac digits.

    /// <summary>Hotkey for plain dictation.</summary>
    public string DictateHotkey { get; set; } = "";

    /// <summary>Dictate hotkey on (Mac isEnabled). Use <see cref="ActiveDictateHotkey"/> to register.</summary>
    public bool DictateHotkeyEnabled { get; set; }

    /// <summary>Hotkey for Dictate Prompt.</summary>
    public string PromptHotkey { get; set; } = "";

    /// <summary>Dictate Prompt hotkey on. Use <see cref="ActivePromptHotkey"/> to register.</summary>
    public bool PromptHotkeyEnabled { get; set; }

    /// <summary>Hotkey that appends the selected text to the Glossary. With the flag off it is only the suggestion.</summary>
    public string GlossaryHotkey { get; set; } = "";

    /// <summary>Glossary hotkey on. Off for new installs, like the Mac. Use <see cref="ActiveGlossaryHotkey"/> to register.</summary>
    public bool GlossaryHotkeyEnabled { get; set; }

    /// <summary>Hotkey that opens the settings window; "" = unassigned.</summary>
    public string OpenSettingsHotkey { get; set; } = "";

    /// <summary>Open-settings hotkey on. Use <see cref="ActiveOpenSettingsHotkey"/> to register.</summary>
    public bool OpenSettingsHotkeyEnabled { get; set; }

    /// <summary>Hotkey that reads the selected text aloud; "" = unassigned.</summary>
    public string ReadAloudHotkey { get; set; } = "";

    /// <summary>Read Aloud hotkey on. Use <see cref="ActiveReadAloudHotkey"/> to register.</summary>
    public bool ReadAloudHotkeyEnabled { get; set; }

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
    public string ReadAloudVoiceFor(TtsProvider provider) => provider switch
    {
        TtsProvider.Gemini => ReadAloud.NormalizeVoice(provider, ReadAloudVoiceGemini),
        TtsProvider.OpenAI => ReadAloud.NormalizeVoice(provider, ReadAloudVoiceOpenAi),
        _ => ReadAloud.NormalizeVoice(provider, ReadAloudVoiceSystem),
    };

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
    public string ScreenshotHotkey { get; set; } = "";

    /// <summary>Screenshot hotkey on. Use <see cref="ActiveScreenshotHotkey"/> to register.</summary>
    public bool ScreenshotHotkeyEnabled { get; set; }

    /// <summary>Id of the template the generic hotkey applied last; "" = none yet.</summary>
    public string LastTemplateId { get; set; } = "";

    /// <summary>Hotkey that starts/stops a Live Meeting. Empty = no hotkey (unassigned by default).</summary>
    public string MeetingHotkey { get; set; } = "";

    /// <summary>Live diktieren (macOS Dictate Live). "" = unassigned. New installs get the suggestion Alt+5, switched off like the Mac.</summary>
    public string LiveDictateHotkey { get; set; } = "";

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

    /// <summary>Stable (promoted releases only, default) or Latest (every release at once). No downgrade either way.</summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// What happens to the clipboard after auto-paste (ignored when <see cref="AutoPaste"/> is off). New installs leave
    /// the dictation there like the Mac; older files map their "restoreClipboard" switch (see <see cref="Choices.AfterPasteFromFile"/>).
    /// </summary>
    public ClipboardAfterPaste ClipboardAfterPaste { get; set; } = ClipboardAfterPaste.KeepDictation;

    /// <summary>
    /// The old switch, kept as a view of <see cref="ClipboardAfterPaste"/>: true = restore the previous clipboard.
    /// Setting it false turns "restore" into "keep" and leaves "clear" alone. Still written to the file for older versions.
    /// </summary>
    [JsonIgnore]
    public bool RestoreClipboard
    {
        get => ClipboardAfterPaste == ClipboardAfterPaste.RestorePrevious;
        set
        {
            if (value) ClipboardAfterPaste = ClipboardAfterPaste.RestorePrevious;
            else if (ClipboardAfterPaste == ClipboardAfterPaste.RestorePrevious) ClipboardAfterPaste = ClipboardAfterPaste.KeepDictation;
        }
    }

    /// <summary>
    /// The welcome tour was finished with "Fertig" (Mac hasCompletedOnboarding). Closing the window is not finishing.
    /// Files from before the tour count as finished when that install was set up (<see cref="WelcomeTour.CompletedForExistingFile"/>).
    /// </summary>
    public bool WelcomeCompleted { get; set; }

    /// <summary>Exe paths for which the user declined "an seinen festen Platz legen": asked once per path.</summary>
    public List<string> MoveOfferDismissed { get; set; } = new();

    /// <summary>The tour step the user last reached, so the next start resumes there (Mac onboardingCurrentStep).</summary>
    public int WelcomeStep { get; set; }

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

    /// <summary>
    /// Loads the settings. A missing file yields defaults. An empty or corrupt file is kept as .bak and the
    /// previous good file (<c>settings.json.prev</c>) is used before defaults.
    /// </summary>
    public static Settings Load(string path, ISecretProtector protector)
    {
        var s = new Settings(path, protector);
        // No file = fresh install: the Mac-digit defaults above. Save writes the pin flag with them.
        if (!File.Exists(path)) return s;
        // A file = an install that ran before. Until its file carries the pin flag, missing shortcut values mean the
        // old defaults, not the new ones (Mac: the pin in ShortcutConfigManager.loadConfiguration), so an update never
        // moves the user's shortcuts. Also for an unreadable file: it existed, so this PC ran an earlier version.
        s.PinLegacyShortcuts();
        if (!TryReadDto(path, out var dto, out var error))
        {
            Log.Error("settings.json empty or unreadable, trying settings.json.prev", error);
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            if (TryReadDto(PreviousPath(path), out dto, out var prevError))
            {
                Log.Info("settings.json.prev loaded");
                ApplyLoaded(s, dto!, protector);
            }
            else
            {
                Log.Error(prevError == null
                    ? "settings.json.prev missing or empty, using defaults"
                    : "settings.json.prev unreadable, using defaults", prevError);
            }
            return s;
        }
        ApplyLoaded(s, dto!, protector);
        return s;
    }

    /// <summary>True when <paramref name="path"/> holds a settings object. Empty, missing and broken JSON are false.</summary>
    private static bool TryReadDto(string path, out Dto? dto, out Exception? error)
    {
        dto = null;
        error = null;
        try
        {
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return false;
            dto = JsonSerializer.Deserialize<Dto>(text, Json);
            return dto != null;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static void ApplyLoaded(Settings s, Dto dto, ISecretProtector protector)
    {
        // A leftover plaintext key field is imported by ApplyDto and must leave the file on the next write.
        if (s.ApplyDto(dto, protector, exact: false))
        {
            try { s.Save(); } catch { }
        }
    }

    private static string PreviousPath(string path) => path + ".prev";

    /// <summary>
    /// Takes the values of <paramref name="dto"/> over (the one place that knows how a stored file becomes settings). Returns true
    /// when the file still held a plaintext key, which must leave it on the next write. <paramref name="exact"/> = a state from
    /// <see cref="Capture"/>: every field is there, so a blank shortcut key is a value and not a missing field.
    /// </summary>
    private bool ApplyDto(Dto dto, ISecretProtector protector, bool exact)
    {
        var rewrite = ApplyKeys(dto, protector, exact); // Settings.Fields.cs: pin reset and API keys first
        foreach (var field in Fields) field.Apply(this, dto, exact);
        // True when a retired Read Aloud model moved: Load then writes the replacement, so a later
        // Gemini key does not move the user off the Windows voice (Mac #481).
        if (ApplyDerived(dto)) rewrite = true;
        return rewrite;
    }


    /// <summary>The shortcuts an install had before the Mac-digit defaults; every flag on, as it behaved then.</summary>
    private void PinLegacyShortcuts() => HotkeySlots.ApplyLegacy(this);

    /// <summary>The new-install shortcuts.</summary>
    private void ResetShortcutsToDefaults() => HotkeySlots.ApplyDefaults(this);

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

    /// <summary>
    /// Saves. On failure the in-memory settings return to the snapshot taken at the call and
    /// <paramref name="error"/> is German text for the user (the same wording as the settings window).
    /// A caller that changed settings and wants the earlier state back restores its own snapshot afterwards.
    /// </summary>
    public bool TrySave(out string? error)
    {
        var snapshot = Capture();
        try
        {
            Save();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            try { Restore(snapshot); }
            catch (Exception restoreEx) { Log.Error("Restoring settings after a failed save failed", restoreEx); }
            error = ex is DiskFullException disk
                ? disk.Message
                : UserCopy.TechnicalFallback("Die Einstellungen konnten nicht gespeichert werden.", ex.Message);
            Log.Error("Saving settings failed", ex);
            return false;
        }
    }

    private void SaveCore()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var dto = ToDto(_protector.Protect);
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(dto, Json);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true); // the rename must not publish a buffer that a crash can leave empty
        }
        // A good current file becomes settings.json.prev. An empty or corrupt one must not replace that backup.
        if (TryReadDto(_path, out _, out _))
            File.Replace(tmp, _path, PreviousPath(_path), ignoreMetadataErrors: true);
        else
            File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Everything that would be saved, as one string, keys hashed (DPAPI output differs on every call). Equal
    /// fingerprints = equal settings: the app reuses a settings window built in advance only while it matches.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(ToDto(key =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))), Json);

    /// <summary>
    /// Everything that is saved, kept in memory (keys in the clear, never written anywhere), to put back with <see cref="Restore"/>
    /// when writing fails: built on the same Dto as the file, so a new setting is covered the day it gets its JSON field.
    /// </summary>
    public SettingsState Capture() => new(JsonSerializer.Serialize(ToDto(key => key), Json),
        ApiKey.Length == 0 ? _retainedApiKeyBlob : null,
        OpenAiKey.Length == 0 ? _retainedOpenAiKeyBlob : null);

    /// <summary>Puts every saved setting back as it was at <see cref="Capture"/>. Not saved; settings that are not persisted stay as they are.</summary>
    public void Restore(SettingsState state)
    {
        ApplyDto(JsonSerializer.Deserialize<Dto>(state.Json, Json)!, new PlainSecretProtector(), exact: true);
        // The snapshot stores an undecryptable blob as the protected field. The plaintext protector reads that
        // as a key; put the empty key and the blob back.
        if (state.RetainedApiKeyBlob != null)
        {
            _apiKey = "";
            _retainedApiKeyBlob = state.RetainedApiKeyBlob;
        }
        if (state.RetainedOpenAiKeyBlob != null)
        {
            _openAiKey = "";
            _retainedOpenAiKeyBlob = state.RetainedOpenAiKeyBlob;
        }
    }

    private static void SetKey(ref string field, ref string? retainedBlob, string? value)
    {
        var next = value ?? "";
        if (field == next) return;
        field = next;
        retainedBlob = null; // a new value, including clearing a key the user could read, drops the old blob
    }

    private void KeepUndecryptable(bool openAi, string blob)
    {
        if (openAi)
        {
            _openAiKey = "";
            _retainedOpenAiKeyBlob = blob;
        }
        else
        {
            _apiKey = "";
            _retainedApiKeyBlob = blob;
        }
    }

    private Dto ToDto(Func<string, string> protect)
    {
        var dto = new Dto();
        WriteExplicit(dto, protect);
        foreach (var field in Fields) field.Write(this, dto);
        return dto;
    }


    private sealed class TemplateDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("instructions")] public string? Instructions { get; set; }
        [JsonPropertyName("hotkey")] public string? Hotkey { get; set; }
    }

    private sealed partial class Dto
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
        [JsonPropertyName("dictateMode")] public string? DictateMode { get; set; }
        [JsonPropertyName("openAiTranscriptionModel")] public string? OpenAiTranscriptionModel { get; set; }
        [JsonPropertyName("transcriptionLanguage")] public string? TranscriptionLanguage { get; set; }
        [JsonPropertyName("transcriptionTemperature")] public double? TranscriptionTemperature { get; set; }
        [JsonPropertyName("transcriptionThinking")] public string? TranscriptionThinking { get; set; }
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
        [JsonPropertyName("updateChannel")] public string? UpdateChannel { get; set; }
        [JsonPropertyName("restoreClipboard")] public bool? RestoreClipboard { get; set; }
        [JsonPropertyName("clipboardAfterPaste")] public string? ClipboardAfterPaste { get; set; }
        [JsonPropertyName("welcomeCompleted")] public bool? WelcomeCompleted { get; set; }
        [JsonPropertyName("welcomeStep")] public int? WelcomeStep { get; set; }
        [JsonPropertyName("moveOfferDismissed")] public List<string>? MoveOfferDismissed { get; set; }
    }
}
