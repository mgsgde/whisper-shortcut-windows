using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperShortcut.Core;

// The persisted settings as one table (R10b). A simple setting is one row: its JSON key (the Dto property carrying that
// [JsonPropertyName]), how it is read and written on Settings, the new-install value, and what a MISSING field means.
// The two differ on purpose for some rows (Sounds, SendScreenshots, ...): an old file that never had the field keeps the old
// behaviour, a fresh install gets the new default. Every row states both. The Dto class and its JSON names are untouched.
public sealed partial class Settings
{
    /// <summary>One persisted setting: copied from the Dto by <see cref="ApplyDto"/> and onto it by <see cref="ToDto"/>.</summary>
    private abstract class Field
    {
        protected Field(string key) => Key = key;

        public string Key { get; }

        /// <summary>Takes the stored value over, or the row's missing-field value when the file has none.</summary>
        public abstract void Apply(Settings s, Dto dto, bool exact);

        /// <summary>Writes the current value onto the Dto.</summary>
        public abstract void Write(Settings s, Dto dto);

        /// <summary>Null when a fresh Settings holds the row's new-install value, else a message (checked by a test).</summary>
        public abstract string? CheckNewInstallDefault(Settings fresh);
    }

    /// <summary>
    /// A setting whose value is <typeparamref name="TP"/> on Settings and <typeparamref name="TW"/> (the exact Dto property type,
    /// nullable) in the file. Order of events on load: present? decode : missing (or keep the current value), then normalise.
    /// </summary>
    private sealed class Field<TP, TW> : Field
    {
        private readonly Func<Settings, TP> _get;
        private readonly Action<Settings, TP> _set;
        private readonly TP _newDefault;
        private readonly Func<Settings, TP>? _missing;
        private readonly Func<TW, TP> _decode;
        private readonly Func<TP, TW> _encode;
        private readonly Func<TP, TP>? _normalize;
        private readonly bool _normalizeOnWrite;
        private readonly bool _blankIsMissing;
        private readonly Func<Dto, TW> _read;
        private readonly Action<Dto, TW> _write;
        private readonly Func<Dto, TW>? _readLegacy;

        public Field(string json, Func<Settings, TP> get, Action<Settings, TP> set, TP newDefault, Func<Settings, TP>? missing, bool keep,
            Func<TW, TP> decode, Func<TP, TW> encode, Func<TP, TP>? normalize, bool normalizeOnWrite, bool blankIsMissing, string? legacyJson)
            : base(json)
        {
            if ((missing == null) == !keep) throw new ArgumentException($"{json}: state exactly one of missing: or keep:");
            _get = get; _set = set; _newDefault = newDefault; _missing = missing;
            _decode = decode; _encode = encode; _normalize = normalize; _normalizeOnWrite = normalizeOnWrite; _blankIsMissing = blankIsMissing;
            (_read, _write) = DtoAccess<TW>(json);
            if (legacyJson != null) _readLegacy = DtoAccess<TW>(legacyJson).Get;
        }

        public override void Apply(Settings s, Dto dto, bool exact)
        {
            var raw = _read(dto);
            if (raw is null && _readLegacy != null) raw = _readLegacy(dto);
            var present = raw is not null
                && (exact || !_blankIsMissing || raw is not string text || !string.IsNullOrWhiteSpace(text));
            if (!present && _missing == null) return; // keep: the missing-field value is what the settings hold now
            var value = present ? _decode(raw!) : _missing!(s);
            _set(s, _normalize != null ? _normalize(value) : value);
        }

        public override void Write(Settings s, Dto dto)
        {
            var value = _get(s);
            _write(dto, _encode(_normalizeOnWrite && _normalize != null ? _normalize(value) : value));
        }

        public override string? CheckNewInstallDefault(Settings fresh) =>
            JsonSerializer.Serialize(_get(fresh)) == JsonSerializer.Serialize(_newDefault) ? null : $"{Key}: fresh install is not the row's newDefault";
    }

    /// <summary>A provider choice stored as three flat keys (engine token, Gemini model, OpenAI model).</summary>
    private sealed class SelectionField : Field
    {
        private readonly ModelStep _step;
        private readonly Func<Settings, ModelSelection> _get;
        private readonly Action<Settings, ModelSelection> _set;
        private readonly ModelSelection _newDefault;
        private readonly Func<Settings, ModelSelection> _missing;
        private readonly (Func<Dto, string?> Get, Action<Dto, string?> Set) _engine, _gemini, _openAi;

        public SelectionField(ModelStep step, string engineJson, string geminiJson, string openAiJson, Func<Settings, ModelSelection> get,
            Action<Settings, ModelSelection> set, ModelSelection newDefault, Func<Settings, ModelSelection> missing)
            : base(engineJson)
        {
            _step = step; _get = get; _set = set; _newDefault = newDefault; _missing = missing;
            _engine = DtoAccess<string?>(engineJson); _gemini = DtoAccess<string?>(geminiJson); _openAi = DtoAccess<string?>(openAiJson);
        }

        // Each of the three values missing = the same value of `missing` (the same step's factory choice, or the choice an older
        // file's meetings and chat followed); the result is mapped onto the current catalogs.
        public override void Apply(Settings s, Dto dto, bool exact) =>
            _set(s, ModelSelection.Load(_step, _engine.Get(dto), _gemini.Get(dto), _openAi.Get(dto), _missing(s)));

        public override void Write(Settings s, Dto dto)
        {
            var v = _get(s);
            _engine.Set(dto, Choices.Engine.Token(v.Engine));
            _gemini.Set(dto, v.Gemini);
            _openAi.Set(dto, v.OpenAi);
        }

        public override string? CheckNewInstallDefault(Settings fresh) =>
            _get(fresh) == _newDefault ? null : $"{Key}: fresh install is not the row's newDefault";
    }

    // ---- Dto access by JSON key: the Dto stays the one place that names the keys ----

    private static readonly Dictionary<string, PropertyInfo> DtoByJson = typeof(Dto).GetProperties()
        .ToDictionary(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name);

    private static (Func<Dto, TW> Get, Action<Dto, TW> Set) DtoAccess<TW>(string json)
    {
        if (!DtoByJson.TryGetValue(json, out var p) || p.PropertyType != typeof(TW))
            throw new InvalidOperationException($"Dto has no {typeof(TW).Name} property with JSON key \"{json}\"");
        return (p.GetMethod!.CreateDelegate<Func<Dto, TW>>(), p.SetMethod!.CreateDelegate<Action<Dto, TW>>());
    }

    // ---- Row factories. TP = value on Settings, the Dto side is string?, bool?, int?, double? or a list. ----

    private static Field Text(string json, Func<Settings, string> get, Action<Settings, string> set, string newDefault,
        Func<Settings, string>? missing = null, bool keep = false, Func<string, string>? normalize = null, bool normalizeOnWrite = false,
        bool blankIsMissing = false, string? alsoReadFrom = null) =>
        new Field<string, string?>(json, get, set, newDefault, missing, keep, v => v!, v => v, normalize, normalizeOnWrite, blankIsMissing, alsoReadFrom);

    private static Field Flag(string json, Func<Settings, bool> get, Action<Settings, bool> set, bool newDefault,
        Func<Settings, bool>? missing = null, bool keep = false) =>
        new Field<bool, bool?>(json, get, set, newDefault, missing, keep, v => v!.Value, v => v, null, false, false, null);

    private static Field Num<T>(string json, Func<Settings, T> get, Action<Settings, T> set, T newDefault, Func<Settings, T>? missing = null,
        Func<T, T>? normalize = null, bool normalizeOnWrite = false) where T : struct =>
        new Field<T, T?>(json, get, set, newDefault, missing, false, v => v!.Value, v => v, normalize, normalizeOnWrite, false, null);

    private static Field Parsed<T>(string json, Func<Settings, T> get, Action<Settings, T> set, T newDefault, Func<Settings, T> missing,
        Func<string?, T> parse, Func<T, string> token) =>
        new Field<T, string?>(json, get, set, newDefault, missing, false, v => parse(v), v => token(v), null, false, false, null);

    private static Field Items<TP, TW>(string json, Func<Settings, TP> get, Action<Settings, TP> set, TP newDefault, Func<Settings, TP> missing,
        Func<TW, TP> decode, Func<TP, TW> encode) =>
        new Field<TP, TW>(json, get, set, newDefault, missing, false, decode, encode, null, false, false, null);

    private static Field Pick(ModelStep step, string engineJson, string geminiJson, string openAiJson, Func<Settings, ModelSelection> get,
        Action<Settings, ModelSelection> set, ModelSelection newDefault, Func<Settings, ModelSelection> missing) =>
        new SelectionField(step, engineJson, geminiJson, openAiJson, get, set, newDefault, missing);

    private static List<string> Clean(List<string>? list) => list!.Where(t => t != null).ToList();

    /// <summary>
    /// Every simple persisted setting, in load order (a row may read what an earlier row set: the model fallbacks, WelcomeCompleted).
    /// <c>missing:</c> = the value for a file without the field; <c>keep: true</c> = the field missing leaves the value the settings
    /// hold (the constructor's new-install key, or the legacy pin, see <see cref="Load"/>); <c>newDefault</c> = a fresh install.
    /// </summary>
    private static readonly Field[] Fields =
    {
        // ---- Provider and models. A step missing from the file takes the fallback, which for meetings and chat is the choice
        //      older files followed (meetings: Dictate / Dictate Prompt, chat: Dictate Prompt). ----
        Pick(ModelStep.Transcription, "speechEngine", "transcriptionModel", "openAiTranscriptionModel", s => s.Dictate, (s, v) => s.Dictate = v,
            ModelSelection.Default(ModelStep.Transcription), missing: _ => ModelSelection.Default(ModelStep.Transcription)),
        Pick(ModelStep.Text, "textEngine", "promptModel", "openAiPromptModel", s => s.Prompt, (s, v) => s.Prompt = v,
            ModelSelection.Default(ModelStep.Text), missing: _ => ModelSelection.Default(ModelStep.Text)),
        // Before the Meetings page, meetings used the Diktieren and Dictate Prompt providers: an update keeps that.
        Pick(ModelStep.Transcription, "meetingSpeechEngine", "meetingTranscriptionModel", "meetingOpenAiTranscriptionModel",
            s => s.MeetingTranscription, (s, v) => s.MeetingTranscription = v, ModelSelection.Default(ModelStep.Transcription),
            missing: s => s.Dictate),
        Pick(ModelStep.Text, "meetingTextEngine", "meetingSummaryModel", "meetingOpenAiSummaryModel", s => s.MeetingText, (s, v) => s.MeetingText = v,
            ModelSelection.Default(ModelStep.Text) with { Gemini = MeetingOptions.DefaultSummaryModel },
            missing: s => s.Prompt with { Gemini = MeetingOptions.DefaultSummaryModel }),
        Pick(ModelStep.Text, "chatEngine", "chatModel", "chatOpenAiModel", s => s.Chat, (s, v) => s.Chat = v, ModelSelection.Default(ModelStep.Text),
            missing: s => s.Prompt with { Gemini = DefaultPromptModel }),

        // ---- Dictate ----
        Parsed("punctuationMode", s => s.PunctuationMode, (s, v) => s.PunctuationMode = v, PunctuationMode.Automatic,
            missing: _ => PunctuationMode.Automatic, Choices.Punctuation.Parse, Choices.Punctuation.Token),
        Parsed("dictateMode", s => s.DictateMode, (s, v) => s.DictateMode = v, DictateMode.Dictate,
            missing: _ => DictateMode.Dictate, Choices.DictateMode.Parse, Choices.DictateMode.Token),
        Text("transcriptionLanguage", s => s.TranscriptionLanguage, (s, v) => s.TranscriptionLanguage = v, TranscriptionLanguages.Auto,
            missing: _ => TranscriptionLanguages.Auto, normalize: v => TranscriptionLanguages.Normalize(v), normalizeOnWrite: true),
        Num("transcriptionTemperature", s => s.TranscriptionTemperature, (s, v) => s.TranscriptionTemperature = v, TranscriptionTuning.DefaultTemperature,
            missing: _ => TranscriptionTuning.DefaultTemperature, normalize: v => TranscriptionTuning.NormalizeTemperature(v), normalizeOnWrite: true),
        Text("transcriptionThinking", s => s.TranscriptionThinking, (s, v) => s.TranscriptionThinking = v, TranscriptionTuning.DefaultThinking,
            missing: _ => TranscriptionTuning.DefaultThinking, normalize: v => TranscriptionTuning.NormalizeThinking(v), normalizeOnWrite: true),
        // JSON null entries (hand-edited or a half-written file) must not stay in the list: GlossaryEdit.TryAdd would NullRef.
        Items<List<string>, List<string>?>("glossary", s => s.Glossary, (s, v) => s.Glossary = v, new(), missing: _ => new(), Clean, v => v),
        // Files before 0.5.3 stored the template as "promptSystemPrompt", still read.
        Text("dictatePromptTemplate", s => s.DictatePromptTemplate, (s, v) => s.DictatePromptTemplate = v, "",
            missing: _ => "", alsoReadFrom: "promptSystemPrompt"),
        Text("dictateSystemPrompt", s => s.DictateSystemPrompt, (s, v) => s.DictateSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, Prompts.DefaultTranscriptionSystemPrompt)),
        Text("promptModeSystemPrompt", s => s.PromptModeSystemPrompt, (s, v) => s.PromptModeSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, Prompts.DefaultPromptModeSystemPrompt)),
        Text("microphone", s => s.Microphone, (s, v) => s.Microphone = v, "", missing: _ => ""),

        // ---- Shortcuts. keep: = a missing field leaves the constructor's new-install key, or the legacy key Load pinned for a
        //      file without the pin flag (Mac: the pin in ShortcutConfigManager.loadConfiguration). A blank value of the first
        //      three is also "missing" (not for a Restore: there every field is stored); for the rest "" is a stored choice. ----
        Text("dictateHotkey", s => s.DictateHotkey, (s, v) => s.DictateHotkey = v, "Alt+1", keep: true, blankIsMissing: true),
        Text("promptHotkey", s => s.PromptHotkey, (s, v) => s.PromptHotkey = v, "Alt+2", keep: true, blankIsMissing: true),
        Text("glossaryHotkey", s => s.GlossaryHotkey, (s, v) => s.GlossaryHotkey = v, "Alt+7", keep: true, blankIsMissing: true),
        Text("openSettingsHotkey", s => s.OpenSettingsHotkey, (s, v) => s.OpenSettingsHotkey = v, "Alt+0", keep: true),
        Text("screenshotHotkey", s => s.ScreenshotHotkey, (s, v) => s.ScreenshotHotkey = v, "Alt+3", keep: true),
        Text("liveDictateHotkey", s => s.LiveDictateHotkey, (s, v) => s.LiveDictateHotkey = v, "Alt+5", keep: true),
        Text("readAloudHotkey", s => s.ReadAloudHotkey, (s, v) => s.ReadAloudHotkey = v, "Alt+4", keep: true),
        Flag("dictateHotkeyEnabled", s => s.DictateHotkeyEnabled, (s, v) => s.DictateHotkeyEnabled = v, true, keep: true),
        Flag("promptHotkeyEnabled", s => s.PromptHotkeyEnabled, (s, v) => s.PromptHotkeyEnabled = v, true, keep: true),
        Flag("glossaryHotkeyEnabled", s => s.GlossaryHotkeyEnabled, (s, v) => s.GlossaryHotkeyEnabled = v, false, keep: true),
        Flag("screenshotHotkeyEnabled", s => s.ScreenshotHotkeyEnabled, (s, v) => s.ScreenshotHotkeyEnabled = v, true, keep: true),
        Flag("liveDictateHotkeyEnabled", s => s.LiveDictateHotkeyEnabled, (s, v) => s.LiveDictateHotkeyEnabled = v, false, keep: true),
        Flag("openSettingsHotkeyEnabled", s => s.OpenSettingsHotkeyEnabled, (s, v) => s.OpenSettingsHotkeyEnabled = v, true, keep: true),
        Flag("readAloudHotkeyEnabled", s => s.ReadAloudHotkeyEnabled, (s, v) => s.ReadAloudHotkeyEnabled = v, true, keep: true),
        // The chat did not exist in older files: its key is kept (the new one), its switch is on, then ApplyDto turns it off when
        // an older file already uses that key.
        Text("chatHotkey", s => s.ChatHotkey, (s, v) => s.ChatHotkey = v, "Alt+6", keep: true),
        Flag("chatHotkeyEnabled", s => s.ChatHotkeyEnabled, (s, v) => s.ChatHotkeyEnabled = v, true, missing: _ => true),
        Text("meetingHotkey", s => s.MeetingHotkey, (s, v) => s.MeetingHotkey = v, "", missing: _ => ""),
        Text("templateHotkey", s => s.TemplateHotkey, (s, v) => s.TemplateHotkey = v, "", missing: _ => ""),
        Flag("rightCtrlDictation", s => s.RightCtrlDictation, (s, v) => s.RightCtrlDictation = v, true, missing: _ => true),

        // ---- Read Aloud ----
        Text("readAloudModel", s => s.ReadAloudModel, (s, v) => s.ReadAloudModel = v, ReadAloud.DefaultModel,
            missing: _ => ReadAloud.DefaultModel, normalize: v => ReadAloud.NormalizeModel(v)),
        Text("readAloudVoiceGemini", s => s.ReadAloudVoiceGemini, (s, v) => s.ReadAloudVoiceGemini = v, ReadAloud.DefaultVoice(TtsProvider.Gemini),
            missing: _ => ReadAloud.DefaultVoice(TtsProvider.Gemini), normalize: v => ReadAloud.NormalizeVoice(TtsProvider.Gemini, v)),
        Text("readAloudVoiceOpenAi", s => s.ReadAloudVoiceOpenAi, (s, v) => s.ReadAloudVoiceOpenAi = v, ReadAloud.DefaultVoice(TtsProvider.OpenAI),
            missing: _ => ReadAloud.DefaultVoice(TtsProvider.OpenAI), normalize: v => ReadAloud.NormalizeVoice(TtsProvider.OpenAI, v)),
        Text("readAloudVoiceSystem", s => s.ReadAloudVoiceSystem, (s, v) => s.ReadAloudVoiceSystem = v, "",
            missing: _ => "", normalize: v => ReadAloud.NormalizeVoice(TtsProvider.System, v)),
        Num("readAloudSpeed", s => s.ReadAloudSpeed, (s, v) => s.ReadAloudSpeed = v, ReadAloud.DefaultSpeed,
            missing: _ => ReadAloud.DefaultSpeed, normalize: v => ReadAloud.NormalizeSpeed(v)),
        Flag("readAloudSmartRewrite", s => s.ReadAloudSmartRewrite, (s, v) => s.ReadAloudSmartRewrite = v, true, missing: _ => true),
        Text("readAloudRewritePrompt", s => s.ReadAloudRewritePrompt, (s, v) => s.ReadAloudRewritePrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, ReadAloud.DefaultRewritePrompt)),

        // ---- Templates and screenshots ----
        // Missing list = old file: no templates. Null entries are skipped; a blank id gets a fresh one.
        Items<List<DictationTemplate>, List<TemplateDto?>?>("templates", s => s.Templates, (s, v) => s.Templates = v, new(), missing: _ => new(),
            LoadTemplates, v => v.Select(t => (TemplateDto?)new TemplateDto { Id = t.Id, Name = t.Name, Instructions = t.Instructions, Hotkey = t.Hotkey }).ToList()),
        Text("lastTemplateId", s => s.LastTemplateId, (s, v) => s.LastTemplateId = v, "", missing: _ => ""),
        // Installs from before the Mac defaults keep it off (screens of practice software show patient data); a fresh install is on.
        Flag("sendScreenshots", s => s.SendScreenshots, (s, v) => s.SendScreenshots = v, true, missing: _ => false),

        // ---- Meetings ----
        Text("meetingAudioMode", s => s.MeetingAudioMode, (s, v) => s.MeetingAudioMode = v, "auto",
            missing: _ => "auto", normalize: v => MeetingAudioRouting.Name(MeetingAudioRouting.Parse(v))),
        Flag("meetingSummary", s => s.MeetingSummary, (s, v) => s.MeetingSummary = v, true, missing: _ => true),
        Num("meetingChunkSeconds", s => s.MeetingChunkSeconds, (s, v) => s.MeetingChunkSeconds = v, MeetingOptions.DefaultChunkSeconds,
            missing: _ => MeetingOptions.DefaultChunkSeconds, normalize: v => MeetingOptions.NormalizeChunkSeconds(v)),
        Num("meetingSafeguardMinutes", s => s.MeetingSafeguardMinutes, (s, v) => s.MeetingSafeguardMinutes = v, MeetingOptions.DefaultSafeguardMinutes,
            missing: _ => MeetingOptions.DefaultSafeguardMinutes, normalize: v => MeetingOptions.NormalizeSafeguardMinutes(v)),

        // ---- Chat ----
        Text("chatSystemPrompt", s => s.ChatSystemPrompt, (s, v) => s.ChatSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, ChatPrompts.DefaultSystemPrompt)),
        Flag("chatWebSearch", s => s.ChatWebSearch, (s, v) => s.ChatWebSearch = v, true, missing: _ => true),
        Flag("chatCloseOnFocusLoss", s => s.ChatCloseOnFocusLoss, (s, v) => s.ChatCloseOnFocusLoss = v, true, missing: _ => true),

        // ---- Offline, paste, sounds, updates, tour ----
        Flag("offlineMode", s => s.OfflineMode, (s, v) => s.OfflineMode = v, false, missing: _ => false),
        Text("offlineLlmId", s => s.OfflineLlmId, (s, v) => s.OfflineLlmId = v, "", missing: _ => ""),
        Flag("autoPaste", s => s.AutoPaste, (s, v) => s.AutoPaste = v, true, missing: _ => true),
        // Off for a fresh install (the Mac has no tones); an older file without the field keeps them.
        Flag("sounds", s => s.Sounds, (s, v) => s.Sounds = v, false, missing: _ => true),
        Flag("autoUpdateCheck", s => s.AutoUpdateCheck, (s, v) => s.AutoUpdateCheck = v, true, missing: _ => true),
        // Missing = a file from before the tour counts as finished when that install was set up. Reads the keys and OfflineMode
        // (above: rows run in this order, the keys are read first by ApplyDto).
        Flag("welcomeCompleted", s => s.WelcomeCompleted, (s, v) => s.WelcomeCompleted = v, false,
            missing: s => WelcomeTour.CompletedForExistingFile(s.ApiKey, s.OpenAiKey, s.OfflineMode)),
        Num("welcomeStep", s => s.WelcomeStep, (s, v) => s.WelcomeStep = v, 0, missing: _ => 0),
        Items<List<string>, List<string>?>("moveOfferDismissed", s => s.MoveOfferDismissed, (s, v) => s.MoveOfferDismissed = v, new(),
            missing: _ => new(), Clean, v => v),
        // Missing field = old file, give the defaults; an explicit empty list means the user removed them all.
        Items<List<string>, List<string>?>("quickActions", s => s.QuickActions, (s, v) => s.QuickActions = v, new(QuickActionList.Defaults),
            missing: _ => new(QuickActionList.Defaults),
            v => QuickActionList.IsLegacyDefaults(v!) ? new(QuickActionList.Defaults) : QuickActionList.Clean(v!), v => v),
    };

    // ---- The fields that stay explicit, and why ----
    // API keys: protector round-trip, plaintext import. Pin flag: written true by every save, read before everything else.
    // ClipboardAfterPaste / RestoreClipboard: one setting stored under two keys, the legacy key decides when the new one is
    // missing. MeetingSpeechEngineExplicit: its missing value is derived from another stored key (meetingSpeechEngine) and the
    // loaded MeetingTranscription. The two key-clash switches below depend on every shortcut being loaded.

    /// <summary>Applies the explicit fields that must come before the table: the pin reset and the API keys. Returns true when a plaintext key was imported.</summary>
    private bool ApplyKeys(Dto dto, ISecretProtector protector, bool exact)
    {
        if (dto.ShortcutDefaultsAltV1 == true) ResetShortcutsToDefaults();
        // Restore: a key the state does not hold is empty, not whatever the settings hold now (Load starts from "" anyway).
        if (exact) { ApiKey = ""; OpenAiKey = ""; }
        var rewrite = !string.IsNullOrEmpty(dto.ApiKeyPlain) || !string.IsNullOrEmpty(dto.OpenAiKeyPlain);
        if (!string.IsNullOrEmpty(dto.ApiKeyProtected))
        {
            try { ApiKey = protector.Unprotect(dto.ApiKeyProtected) ?? ""; }
            catch (Exception) { ApiKey = ""; }
        }
        else if (!string.IsNullOrEmpty(dto.ApiKeyPlain))
        {
            ApiKey = dto.ApiKeyPlain;
        }
        if (!string.IsNullOrEmpty(dto.OpenAiKeyProtected))
        {
            try { OpenAiKey = protector.Unprotect(dto.OpenAiKeyProtected) ?? ""; }
            catch (Exception) { OpenAiKey = ""; }
        }
        else if (!string.IsNullOrEmpty(dto.OpenAiKeyPlain))
        {
            OpenAiKey = dto.OpenAiKeyPlain;
        }
        return rewrite;
    }

    /// <summary>Applies the explicit fields that need the table's values: the clipboard choice, the meeting-engine flag, the key clashes.</summary>
    private void ApplyDerived(Dto dto)
    {
        // Missing "clipboardAfterPaste" = the old "restoreClipboard" switch decides; both missing = restore (older files did).
        ClipboardAfterPaste = Choices.AfterPasteFromFile(dto.ClipboardAfterPaste, dto.RestoreClipboard);
        // A file from before the flag always stored an engine, the factory default Gemini included, so "chosen" cannot be told
        // from "never chosen" there: Gemini reads as never chosen (follows dictation), any other value was a pick.
        MeetingSpeechEngineExplicit = dto.MeetingSpeechEngineExplicit
            ?? (dto.MeetingSpeechEngine != null && MeetingTranscription.Engine != Engine.Gemini);
        // An update adds the chat key to a file that never had it. It must not take a key the user already uses.
        if (dto.ChatHotkey == null && HotkeySlots.Clashes(this, HotkeyAction.Chat)) ChatHotkeyEnabled = false;
        // Same for the Read Aloud key (checked after the chat key has been placed).
        if (dto.ReadAloudHotkey == null && ReadAloudHotkeyEnabled && HotkeySlots.Clashes(this, HotkeyAction.ReadAloud))
            ReadAloudHotkeyEnabled = false;
    }

    /// <summary>Writes the explicit fields onto the Dto (the table writes the rest).</summary>
    private void WriteExplicit(Dto dto, Func<string, string> protect)
    {
        dto.ApiKeyProtected = ApiKey.Length == 0 ? "" : protect(ApiKey);
        dto.OpenAiKeyProtected = OpenAiKey.Length == 0 ? "" : protect(OpenAiKey);
        // Every shortcut value is written out, so from now on a missing field means the new default.
        dto.ShortcutDefaultsAltV1 = true;
        dto.RestoreClipboard = RestoreClipboard;
        dto.ClipboardAfterPaste = Choices.AfterPaste.Token(ClipboardAfterPaste);
        dto.MeetingSpeechEngineExplicit = MeetingSpeechEngineExplicit;
    }

    /// <summary>JSON keys of the Dto that no row covers (the explicit fields); a test pins this list.</summary>
    internal static IEnumerable<string> ExplicitJsonKeys => DtoByJson.Keys.Except(Fields.Select(f => f.Key))
        .Except(new[] { "meetingOpenAiTranscriptionModel", "meetingTranscriptionModel", "meetingSummaryModel", "meetingOpenAiSummaryModel",
            "transcriptionModel", "openAiTranscriptionModel", "promptModel", "openAiPromptModel", "chatModel", "chatOpenAiModel", "promptSystemPrompt" })
        .Order(StringComparer.Ordinal);

    /// <summary>First row whose new-install value is not what a fresh Settings holds, or null.</summary>
    internal static string? NewInstallMismatch(Settings fresh) => Fields.Select(f => f.CheckNewInstallDefault(fresh)).FirstOrDefault(m => m != null);
}
