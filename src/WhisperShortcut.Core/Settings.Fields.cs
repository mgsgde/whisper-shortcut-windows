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
    /// <summary>What a row says besides how it persists (W11): where its control sits, its AI-app ids and its report line.</summary>
    private sealed record Meta(SettingsPage Page, ControlKind Control, string? Title, string[] Mcp, Func<Settings, string>? Show, string[] Keys);

    /// <summary>One persisted setting: copied from the Dto by <see cref="ApplyDto"/> and onto it by <see cref="ToDto"/>.</summary>
    private abstract class Field
    {
        private Meta? _meta;
        private SettingDescriptor? _descriptor;

        protected Field(string key) => Key = key;

        public string Key { get; }

        /// <summary>Takes the stored value over, or the row's missing-field value when the file has none.</summary>
        public abstract void Apply(Settings s, Dto dto, bool exact);

        /// <summary>Writes the current value onto the Dto.</summary>
        public abstract void Write(Settings s, Dto dto);

        /// <summary>Null when a fresh Settings holds the row's new-install value, else a message (checked by a test).</summary>
        public abstract string? CheckNewInstallDefault(Settings fresh);

        /// <summary>Puts the row's new-install value on a new Settings: the one place a default is written.</summary>
        public abstract void ApplyDefault(Settings s);

        protected abstract SettingType Type { get; }
        protected abstract object? DefaultValue { get; }
        protected abstract object? GetValue(Settings s);
        protected abstract void SetValue(Settings s, object? value);

        /// <summary>The value as it is stored (token, text, number, list): what the report shows unless the row says otherwise.</summary>
        protected abstract object? Stored(Settings s);

        /// <summary>The JSON fields this row owns besides <see cref="Key"/> (a model step's models, a legacy key).</summary>
        protected virtual IEnumerable<string> SubKeys => Array.Empty<string>();

        /// <summary>
        /// Describes the row for the settings window, the AI-app catalog and the report (see <see cref="SettingDescriptor"/>).
        /// <paramref name="title"/> = its report line (null = none), <paramref name="mcp"/> = the AI-app ids it backs,
        /// <paramref name="show"/> = how the report writes its value (default: the stored value), <paramref name="keys"/> = more fields it owns.
        /// </summary>
        public Field Is(SettingsPage page, ControlKind control, string? title = null, string[]? mcp = null, Func<Settings, string>? show = null,
            string[]? keys = null)
        {
            _meta = new Meta(page, control, title, mcp ?? Array.Empty<string>(), show, keys ?? Array.Empty<string>());
            return this;
        }

        /// <summary>Null for a row that is only half of another descriptor (a shortcut's on switch).</summary>
        public SettingDescriptor? Descriptor => _meta == null ? null : _descriptor ??= new SettingDescriptor(Key,
            new[] { Key }.Concat(SubKeys).Concat(_meta.Keys).ToList(), Type, DefaultValue, _meta.Mcp, _meta.Title, _meta.Control, _meta.Page,
            GetValue, SetValue, _meta.Show ?? (s => SettingDescriptor.Format(Stored(s))));
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
        private readonly string? _legacyJson;
        private readonly SettingType _type;
        private readonly Func<TP, TP>? _copy;

        public Field(SettingType type, string json, Func<Settings, TP> get, Action<Settings, TP> set, TP newDefault, Func<Settings, TP>? missing, bool keep,
            Func<TW, TP> decode, Func<TP, TW> encode, Func<TP, TP>? normalize, bool normalizeOnWrite, bool blankIsMissing, string? legacyJson,
            Func<TP, TP>? copy = null)
            : base(json)
        {
            _type = type; _legacyJson = legacyJson; _copy = copy;
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

        // A list default is copied: every Settings owns its list.
        public override void ApplyDefault(Settings s) => _set(s, _copy != null ? _copy(_newDefault) : _newDefault);

        protected override SettingType Type => _type;
        protected override object? DefaultValue => _newDefault;
        protected override object? GetValue(Settings s) => _get(s);
        protected override void SetValue(Settings s, object? value) => _set(s, (TP)value!);
        protected override object? Stored(Settings s) => _encode(_get(s));
        protected override IEnumerable<string> SubKeys => _legacyJson == null ? Array.Empty<string>() : new[] { _legacyJson };
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
        private readonly string _geminiJson, _openAiJson;

        public SelectionField(ModelStep step, string engineJson, string geminiJson, string openAiJson, Func<Settings, ModelSelection> get,
            Action<Settings, ModelSelection> set, ModelSelection newDefault, Func<Settings, ModelSelection> missing)
            : base(engineJson)
        {
            _step = step; _get = get; _set = set; _newDefault = newDefault; _missing = missing;
            _geminiJson = geminiJson; _openAiJson = openAiJson;
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

        public override void ApplyDefault(Settings s) => _set(s, _newDefault);

        protected override SettingType Type => SettingType.ModelStep;
        protected override object? DefaultValue => _newDefault;
        protected override object? GetValue(Settings s) => _get(s);
        protected override void SetValue(Settings s, object? value) => _set(s, (ModelSelection)value!);
        protected override object? Stored(Settings s) => _get(s);
        protected override IEnumerable<string> SubKeys => new[] { _geminiJson, _openAiJson };
    }

    /// <summary>
    /// A setting that is described here but loaded and saved by code (<see cref="ApplyKeys"/>, <see cref="ApplyDerived"/>,
    /// <see cref="WriteExplicit"/>): the API keys, the clipboard choice, the pin flag, the meeting-engine flag. Persistence stays
    /// explicit; the row gives it its descriptor and, where it is a Settings property, its new-install value.
    /// </summary>
    private sealed class ExplicitField : Field
    {
        private readonly SettingType _type;
        private readonly object? _default;
        private readonly Func<Settings, object?>? _get;
        private readonly Action<Settings, object?>? _set;

        public ExplicitField(SettingType type, string json, object? newDefault = null, Func<Settings, object?>? get = null,
            Action<Settings, object?>? set = null) : base(json)
        {
            _type = type; _default = newDefault; _get = get; _set = set;
        }

        public override void Apply(Settings s, Dto dto, bool exact) { }

        public override void Write(Settings s, Dto dto) { }

        public override string? CheckNewInstallDefault(Settings fresh) =>
            _get == null || JsonSerializer.Serialize(_get(fresh)) == JsonSerializer.Serialize(_default) ? null
                : $"{Key}: fresh install is not the row's newDefault";

        public override void ApplyDefault(Settings s)
        {
            if (_set != null) _set(s, _default);
        }

        protected override SettingType Type => _type;
        protected override object? DefaultValue => _default;
        protected override object? GetValue(Settings s) => _get?.Invoke(s);
        protected override void SetValue(Settings s, object? value) => _set?.Invoke(s, value);
        protected override object? Stored(Settings s) => _get?.Invoke(s);
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
        new Field<string, string?>(SettingType.Text, json, get, set, newDefault, missing, keep, v => v!, v => v, normalize, normalizeOnWrite, blankIsMissing, alsoReadFrom);

    private static Field Flag(string json, Func<Settings, bool> get, Action<Settings, bool> set, bool newDefault,
        Func<Settings, bool>? missing = null, bool keep = false) =>
        new Field<bool, bool?>(SettingType.Flag, json, get, set, newDefault, missing, keep, v => v!.Value, v => v, null, false, false, null);

    private static Field Num<T>(string json, Func<Settings, T> get, Action<Settings, T> set, T newDefault, Func<Settings, T>? missing = null,
        Func<T, T>? normalize = null, bool normalizeOnWrite = false) where T : struct =>
        new Field<T, T?>(SettingType.Number, json, get, set, newDefault, missing, false, v => v!.Value, v => v, normalize, normalizeOnWrite, false, null);

    private static Field Parsed<T>(string json, Func<Settings, T> get, Action<Settings, T> set, T newDefault, Func<Settings, T> missing,
        Func<string?, T> parse, Func<T, string> token) =>
        new Field<T, string?>(SettingType.Choice, json, get, set, newDefault, missing, false, v => parse(v), v => token(v), null, false, false, null);

    private static Field Items<TP, TW>(string json, Func<Settings, TP> get, Action<Settings, TP> set, TP newDefault, Func<Settings, TP> missing,
        Func<TW, TP> decode, Func<TP, TW> encode, Func<TP, TP> copy) =>
        new Field<TP, TW>(SettingType.List, json, get, set, newDefault, missing, false, decode, encode, null, false, false, null, copy);

    private static Field Pick(ModelStep step, string engineJson, string geminiJson, string openAiJson, Func<Settings, ModelSelection> get,
        Action<Settings, ModelSelection> set, ModelSelection newDefault, Func<Settings, ModelSelection> missing) =>
        new SelectionField(step, engineJson, geminiJson, openAiJson, get, set, newDefault, missing);

    private static List<string> Clean(List<string>? list) => list!.Where(t => t != null).ToList();

    /// <summary>Where a shortcut's field sits in the settings window.</summary>
    private static SettingsPage PageOf(HotkeyAction action) => action switch
    {
        HotkeyAction.ReadAloud => SettingsPage.ReadAloud,
        HotkeyAction.Chat => SettingsPage.Chat,
        HotkeyAction.TemplatePicker => SettingsPage.Templates,
        HotkeyAction.Meeting => SettingsPage.Meetings,
        _ => SettingsPage.General,
    };

    /// <summary>
    /// The two rows (key, on switch) of every <see cref="HotkeySlots.All"/> row; a shortcut without an on switch has the key row only.
    /// With a switch a missing field keeps what the settings hold now (the constructor's new-install key, or the legacy pin);
    /// without one a missing key is "". The key row is the shortcut's descriptor and owns the on switch's JSON field too.
    /// </summary>
    private static IEnumerable<Field> HotkeyRows() => HotkeySlots.All.SelectMany(slot =>
    {
        // The report and AI apps see one value: the key when the shortcut is on, "Aus" when it is off or unassigned.
        string Shown(Settings s) => !slot.IsOn(s) || string.IsNullOrWhiteSpace(slot.Get(s)) ? "Aus"
            : Mcp.HotkeyRules.TryNormalize(slot.Get(s), out var n) ? Mcp.HotkeyRules.Display(n) : slot.Get(s);
        var page = PageOf(slot.Action);
        var control = slot.Action == HotkeyAction.Meeting ? ControlKind.None : ControlKind.Hotkey; // the Meeting key is set from the tray menu
        var title = "Tastenkürzel " + slot.Owner;
        var mcp = slot.McpId == null ? null : new[] { slot.McpId };
        var key = slot.HasSwitch
            ? Text(slot.JsonKey, s => s.Key(slot.Action), (s, v) => s.SetKey(slot.Action, v), slot.NewDefault, keep: true,
                blankIsMissing: slot.BlankKeyIsMissing)
            : Text(slot.JsonKey, s => s.Key(slot.Action), (s, v) => s.SetKey(slot.Action, v), slot.NewDefault, missing: _ => "");
        key.Is(page, control, title, mcp, Shown, slot.JsonOnKey == null ? null : new[] { slot.JsonOnKey });
        return slot.HasSwitch
            ? new[] { key, Flag(slot.JsonOnKey!, s => s.IsOn(slot.Action), (s, v) => s.SetOn(slot.Action, v), slot.NewEnabled, keep: true) }
            : new[] { key };
    });

    private static string ModelChoiceText(ModelSelection v) =>
        $"{Choices.Engine.Label(v.Engine)} (Gemini {v.Gemini}, OpenAI {v.OpenAi})";

    /// <summary>
    /// Every simple persisted setting, in load order (a row may read what an earlier row set: the model fallbacks, WelcomeCompleted).
    /// <c>missing:</c> = the value for a file without the field; <c>keep: true</c> = the field missing leaves the value the settings
    /// hold (the constructor's new-install key, or the legacy pin, see <see cref="Load"/>); <c>newDefault</c> = a fresh install, also
    /// what a new <see cref="Settings"/> starts with (nothing else states a default). <c>.Is(...)</c> describes the row for the window,
    /// the AI-app catalog and the report (see <see cref="SettingDescriptor"/>).
    /// </summary>
    private static readonly Field[] Fields =
    [
        // ---- Provider and models. A step missing from the file takes the fallback, which for meetings and chat is the choice
        //      older files followed (meetings: Dictate / Dictate Prompt, chat: Dictate Prompt). ----
        Pick(ModelStep.Transcription, "speechEngine", "transcriptionModel", "openAiTranscriptionModel", s => s.Dictate, (s, v) => s.Dictate = v,
            ModelSelection.Default(ModelStep.Transcription), missing: _ => ModelSelection.Default(ModelStep.Transcription))
            .Is(SettingsPage.Dictate, ControlKind.ModelStep, "Spracherkennung (Anbieter und Modelle)",
                ["dictation.engine", "dictation.transcription_model", "dictation.openai_transcription_model"], s => ModelChoiceText(s.Dictate)),
        Pick(ModelStep.Text, "textEngine", "promptModel", "openAiPromptModel", s => s.Prompt, (s, v) => s.Prompt = v,
            ModelSelection.Default(ModelStep.Text), missing: _ => ModelSelection.Default(ModelStep.Text))
            .Is(SettingsPage.DictatePrompt, ControlKind.ModelStep, "Texte bearbeiten (Anbieter und Modelle)",
                ["dictate_prompt.engine", "dictate_prompt.model", "dictate_prompt.openai_model"], s => ModelChoiceText(s.Prompt)),
        // Before the Meetings page, meetings used the Diktieren and Dictate Prompt providers: an update keeps that.
        Pick(ModelStep.Transcription, "meetingSpeechEngine", "meetingTranscriptionModel", "meetingOpenAiTranscriptionModel",
            s => s.MeetingTranscription, (s, v) => s.MeetingTranscription = v, ModelSelection.Default(ModelStep.Transcription),
            missing: s => s.Dictate)
            .Is(SettingsPage.Meetings, ControlKind.ModelStep, "Meetings: Spracherkennung (Anbieter und Modelle)",
                show: s => ModelChoiceText(s.MeetingTranscription with { Engine = s.ConfiguredMeetingSpeechEngine })),
        Pick(ModelStep.Text, "meetingTextEngine", "meetingSummaryModel", "meetingOpenAiSummaryModel", s => s.MeetingText, (s, v) => s.MeetingText = v,
            ModelSelection.Default(ModelStep.Text) with { Gemini = MeetingOptions.DefaultSummaryModel },
            missing: s => s.Prompt with { Gemini = MeetingOptions.DefaultSummaryModel })
            .Is(SettingsPage.Meetings, ControlKind.ModelStep, "Meetings: Zusammenfassung (Anbieter und Modelle)",
                show: s => ModelChoiceText(s.MeetingText)),
        Pick(ModelStep.Text, "chatEngine", "chatModel", "chatOpenAiModel", s => s.Chat, (s, v) => s.Chat = v, ModelSelection.Default(ModelStep.Text),
            missing: s => s.Prompt with { Gemini = DefaultPromptModel })
            .Is(SettingsPage.Chat, ControlKind.ModelStep, "Chat: Anbieter und Modelle", show: s => ModelChoiceText(s.Chat)),

        // ---- Dictate ----
        Parsed("punctuationMode", s => s.PunctuationMode, (s, v) => s.PunctuationMode = v, PunctuationMode.Automatic,
            missing: _ => PunctuationMode.Automatic, Choices.Punctuation.Parse, Choices.Punctuation.Token)
            .Is(SettingsPage.Dictate, ControlKind.Choice, "Satzzeichen (Offline-Spracherkennung)", ["dictation.punctuation"],
                s => Choices.Punctuation.Label(s.PunctuationMode)),
        Parsed("dictateMode", s => s.DictateMode, (s, v) => s.DictateMode = v, DictateMode.Dictate,
            missing: _ => DictateMode.Dictate, Choices.DictateMode.Parse, Choices.DictateMode.Token)
            .Is(SettingsPage.Dictate, ControlKind.Choice, "Modus", ["dictation.mode"], s => Choices.DictateMode.Label(s.DictateMode)),
        Text("transcriptionLanguage", s => s.TranscriptionLanguage, (s, v) => s.TranscriptionLanguage = v, TranscriptionLanguages.Auto,
            missing: _ => TranscriptionLanguages.Auto, normalize: v => TranscriptionLanguages.Normalize(v), normalizeOnWrite: true)
            .Is(SettingsPage.Dictate, ControlKind.Choice, "Sprache (OpenAI-Transkription)", ["dictation.language"]),
        Num("transcriptionTemperature", s => s.TranscriptionTemperature, (s, v) => s.TranscriptionTemperature = v, TranscriptionTuning.DefaultTemperature,
            missing: _ => TranscriptionTuning.DefaultTemperature, normalize: v => TranscriptionTuning.NormalizeTemperature(v), normalizeOnWrite: true)
            .Is(SettingsPage.Dictate, ControlKind.Choice, "Temperatur (Gemini-Transkription)", ["dictation.temperature"],
                s => TranscriptionTuning.Format(s.TranscriptionTemperature)),
        Text("transcriptionThinking", s => s.TranscriptionThinking, (s, v) => s.TranscriptionThinking = v, TranscriptionTuning.DefaultThinking,
            missing: _ => TranscriptionTuning.DefaultThinking, normalize: v => TranscriptionTuning.NormalizeThinking(v), normalizeOnWrite: true)
            .Is(SettingsPage.Dictate, ControlKind.Choice, "Denkaufwand (Gemini-Transkription)", ["dictation.thinking"]),
        // JSON null entries (hand-edited or a half-written file) must not stay in the list: GlossaryEdit.TryAdd would NullRef.
        Items<List<string>, List<string>?>("glossary", s => s.Glossary, (s, v) => s.Glossary = v, new(), missing: _ => new(), Clean, v => v, v => new(v))
            .Is(SettingsPage.Dictate, ControlKind.Text),
        // Files before 0.5.3 stored the template as "promptSystemPrompt", still read.
        Text("dictatePromptTemplate", s => s.DictatePromptTemplate, (s, v) => s.DictatePromptTemplate = v, "",
            missing: _ => "", alsoReadFrom: "promptSystemPrompt")
            .Is(SettingsPage.DictatePrompt, ControlKind.Text),
        Text("dictateSystemPrompt", s => s.DictateSystemPrompt, (s, v) => s.DictateSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, Prompts.DefaultTranscriptionSystemPrompt))
            .Is(SettingsPage.Dictate, ControlKind.Text),
        Text("promptModeSystemPrompt", s => s.PromptModeSystemPrompt, (s, v) => s.PromptModeSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, Prompts.DefaultPromptModeSystemPrompt))
            .Is(SettingsPage.DictatePrompt, ControlKind.Text),
        Text("microphone", s => s.Microphone, (s, v) => s.Microphone = v, "", missing: _ => "")
            .Is(SettingsPage.General, ControlKind.Choice, "Mikrofon", show: s => s.Microphone.Length == 0 ? "Windows-Standard" : s.Microphone),

        // ---- Shortcuts. keep: = a missing field leaves the constructor's new-install key, or the legacy key Load pinned for a
        //      file without the pin flag (Mac: the pin in ShortcutConfigManager.loadConfiguration). A blank value of the first
        //      three is also "missing" (not for a Restore: there every field is stored); for the rest "" is a stored choice. ----
        ..HotkeyRows(),
        Flag("rightCtrlDictation", s => s.RightCtrlDictation, (s, v) => s.RightCtrlDictation = v, true, missing: _ => true)
            .Is(SettingsPage.General, ControlKind.Toggle, "Rechte Strg-Taste (Diktieren)"),

        // ---- Read Aloud ----
        Text("readAloudModel", s => s.ReadAloudModel, (s, v) => s.ReadAloudModel = v, ReadAloud.DefaultModel,
            missing: _ => ReadAloud.DefaultModel, normalize: v => ReadAloud.NormalizeModel(v))
            .Is(SettingsPage.ReadAloud, ControlKind.Choice, "Vorlesen: Modell", ["read_aloud.model"]),
        Text("readAloudVoiceGemini", s => s.ReadAloudVoiceGemini, (s, v) => s.ReadAloudVoiceGemini = v, ReadAloud.DefaultVoice(TtsProvider.Gemini),
            missing: _ => ReadAloud.DefaultVoice(TtsProvider.Gemini), normalize: v => ReadAloud.NormalizeVoice(TtsProvider.Gemini, v))
            .Is(SettingsPage.ReadAloud, ControlKind.Choice, "Vorlesen: Gemini-Stimme", ["read_aloud.voice.gemini"]),
        Text("readAloudVoiceOpenAi", s => s.ReadAloudVoiceOpenAi, (s, v) => s.ReadAloudVoiceOpenAi = v, ReadAloud.DefaultVoice(TtsProvider.OpenAI),
            missing: _ => ReadAloud.DefaultVoice(TtsProvider.OpenAI), normalize: v => ReadAloud.NormalizeVoice(TtsProvider.OpenAI, v))
            .Is(SettingsPage.ReadAloud, ControlKind.Choice, "Vorlesen: OpenAI-Stimme", ["read_aloud.voice.openai"]),
        Text("readAloudVoiceSystem", s => s.ReadAloudVoiceSystem, (s, v) => s.ReadAloudVoiceSystem = v, "",
            missing: _ => "", normalize: v => ReadAloud.NormalizeVoice(TtsProvider.System, v))
            .Is(SettingsPage.ReadAloud, ControlKind.Choice, "Vorlesen: Windows-Stimme", show: s => s.ReadAloudVoiceSystem.Length == 0 ? "Windows-Standard" : s.ReadAloudVoiceSystem),
        Num("readAloudSpeed", s => s.ReadAloudSpeed, (s, v) => s.ReadAloudSpeed = v, ReadAloud.DefaultSpeed,
            missing: _ => ReadAloud.DefaultSpeed, normalize: v => ReadAloud.NormalizeSpeed(v))
            .Is(SettingsPage.ReadAloud, ControlKind.Choice, "Vorlesen: Geschwindigkeit", ["read_aloud.speed"],
                s => ReadAloud.SpeedLabel(ReadAloud.NormalizeSpeed(s.ReadAloudSpeed))),
        Flag("readAloudSmartRewrite", s => s.ReadAloudSmartRewrite, (s, v) => s.ReadAloudSmartRewrite = v, true, missing: _ => true)
            .Is(SettingsPage.ReadAloud, ControlKind.Toggle, "Vorlesen: intelligentes Umschreiben", ["read_aloud.smart_rewrite"]),
        Text("readAloudRewritePrompt", s => s.ReadAloudRewritePrompt, (s, v) => s.ReadAloudRewritePrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, ReadAloud.DefaultRewritePrompt))
            .Is(SettingsPage.ReadAloud, ControlKind.Text),

        // ---- Templates and screenshots ----
        // Missing list = old file: no templates. Null entries are skipped; a blank id gets a fresh one.
        Items<List<DictationTemplate>, List<TemplateDto?>?>("templates", s => s.Templates, (s, v) => s.Templates = v, new(), missing: _ => new(),
            LoadTemplates, v => v.Select(t => (TemplateDto?)new TemplateDto { Id = t.Id, Name = t.Name, Instructions = t.Instructions, Hotkey = t.Hotkey }).ToList(),
            v => new(v))
            .Is(SettingsPage.Templates, ControlKind.List),
        Text("lastTemplateId", s => s.LastTemplateId, (s, v) => s.LastTemplateId = v, "", missing: _ => "")
            .Is(SettingsPage.Internal, ControlKind.None),
        // Installs from before the Mac defaults keep it off (screens of practice software show patient data); a fresh install is on.
        Flag("sendScreenshots", s => s.SendScreenshots, (s, v) => s.SendScreenshots = v, true, missing: _ => false)
            .Is(SettingsPage.DictatePrompt, ControlKind.Toggle, "Bildschirmfoto mitschicken"),

        // ---- Meetings ----
        Text("meetingAudioMode", s => s.MeetingAudioMode, (s, v) => s.MeetingAudioMode = v, "auto",
            missing: _ => "auto", normalize: v => MeetingAudioRouting.Name(MeetingAudioRouting.Parse(v)))
            .Is(SettingsPage.Meetings, ControlKind.None, "Meetings: Audioquelle"),
        Flag("meetingSummary", s => s.MeetingSummary, (s, v) => s.MeetingSummary = v, true, missing: _ => true)
            .Is(SettingsPage.Meetings, ControlKind.None, "Meetings: Zusammenfassung schreiben"),
        Num("meetingChunkSeconds", s => s.MeetingChunkSeconds, (s, v) => s.MeetingChunkSeconds = v, MeetingOptions.DefaultChunkSeconds,
            missing: _ => MeetingOptions.DefaultChunkSeconds, normalize: v => MeetingOptions.NormalizeChunkSeconds(v))
            .Is(SettingsPage.Meetings, ControlKind.Choice, "Meetings: Chunk-Länge (Sekunden)"),
        Num("meetingSafeguardMinutes", s => s.MeetingSafeguardMinutes, (s, v) => s.MeetingSafeguardMinutes = v, MeetingOptions.DefaultSafeguardMinutes,
            missing: _ => MeetingOptions.DefaultSafeguardMinutes, normalize: v => MeetingOptions.NormalizeSafeguardMinutes(v))
            .Is(SettingsPage.Meetings, ControlKind.Choice, "Meetings: Nachfrage nach Minuten (0 = nie)"),

        // ---- Chat ----
        Text("chatSystemPrompt", s => s.ChatSystemPrompt, (s, v) => s.ChatSystemPrompt = v, "",
            missing: _ => "", normalize: v => CustomOrEmpty(v, ChatPrompts.DefaultSystemPrompt))
            .Is(SettingsPage.Chat, ControlKind.Text),
        Flag("chatWebSearch", s => s.ChatWebSearch, (s, v) => s.ChatWebSearch = v, true, missing: _ => true)
            .Is(SettingsPage.Chat, ControlKind.Toggle, "Chat: Websuche"),
        Flag("chatCloseOnFocusLoss", s => s.ChatCloseOnFocusLoss, (s, v) => s.ChatCloseOnFocusLoss = v, true, missing: _ => true)
            .Is(SettingsPage.Chat, ControlKind.Toggle, "Chat: schließt beim Fokuswechsel"),

        // ---- Offline, paste, sounds, updates, tour ----
        // Offline mode has its own line in the report header.
        Flag("offlineMode", s => s.OfflineMode, (s, v) => s.OfflineMode = v, false, missing: _ => false)
            .Is(SettingsPage.Privacy, ControlKind.Toggle),
        Text("offlineLlmId", s => s.OfflineLlmId, (s, v) => s.OfflineLlmId = v, "", missing: _ => "")
            .Is(SettingsPage.Privacy, ControlKind.Choice, "Offline-Sprachmodell", ["offline.llm"],
                s => OfflineCatalog.LlmById(s.OfflineLlmId).Id),
        Flag("autoPaste", s => s.AutoPaste, (s, v) => s.AutoPaste = v, true, missing: _ => true)
            .Is(SettingsPage.General, ControlKind.Toggle, "Automatisch einfügen", ["clipboard.auto_paste"]),
        // Off for a fresh install (the Mac has no tones); an older file without the field keeps them.
        Flag("sounds", s => s.Sounds, (s, v) => s.Sounds = v, false, missing: _ => true)
            .Is(SettingsPage.General, ControlKind.Toggle, "Signaltöne", ["general.sounds"]),
        Flag("autoUpdateCheck", s => s.AutoUpdateCheck, (s, v) => s.AutoUpdateCheck = v, true, missing: _ => true)
            .Is(SettingsPage.General, ControlKind.Toggle, "Automatisch nach Updates suchen", ["general.auto_update_check"]),
        // Missing or unknown = Stable: existing installs stay on the promoted releases, no migration.
        Parsed("updateChannel", s => s.UpdateChannel, (s, v) => s.UpdateChannel = v, UpdateChannel.Stable,
            missing: _ => UpdateChannel.Stable, Choices.UpdateChannel.Parse, Choices.UpdateChannel.Token)
            .Is(SettingsPage.General, ControlKind.Choice, "Update-Kanal", show: s => Choices.UpdateChannel.Label(s.UpdateChannel)),
        // Missing = a file from before the tour counts as finished when that install was set up. Reads the keys and OfflineMode
        // (above: rows run in this order, the keys are read first by ApplyDto).
        Flag("welcomeCompleted", s => s.WelcomeCompleted, (s, v) => s.WelcomeCompleted = v, false,
            missing: s => WelcomeTour.CompletedForExistingFile(s.ApiKey, s.OpenAiKey, s.OfflineMode))
            .Is(SettingsPage.Internal, ControlKind.None),
        Num("welcomeStep", s => s.WelcomeStep, (s, v) => s.WelcomeStep = v, 0, missing: _ => 0)
            .Is(SettingsPage.Internal, ControlKind.None),
        Items<List<string>, List<string>?>("moveOfferDismissed", s => s.MoveOfferDismissed, (s, v) => s.MoveOfferDismissed = v, new(),
            missing: _ => new(), Clean, v => v, v => new(v))
            .Is(SettingsPage.Internal, ControlKind.None),
        // Missing field = old file, give the defaults; an explicit empty list means the user removed them all.
        Items<List<string>, List<string>?>("quickActions", s => s.QuickActions, (s, v) => s.QuickActions = v, new(QuickActionList.Defaults),
            missing: _ => new(QuickActionList.Defaults),
            v => QuickActionList.IsLegacyDefaults(v!) ? new(QuickActionList.Defaults) : QuickActionList.Clean(v!), v => v, v => new(v))
            .Is(SettingsPage.DictatePrompt, ControlKind.List),
    ];

    /// <summary>
    /// The settings that are loaded and saved by code, described like the rows (see <see cref="ExplicitField"/>). The keys: never in a
    /// report or to AI apps (the descriptor refuses a title or an id). The pin flag has no Settings property.
    /// </summary>
    private static readonly Field[] ExplicitFields =
    [
        new ExplicitField(SettingType.Secret, "apiKeyProtected", "", s => s.ApiKey, (s, v) => s.ApiKey = (string)v!)
            .Is(SettingsPage.Privacy, ControlKind.Text, keys: ["apiKey"]),
        new ExplicitField(SettingType.Secret, "openAiKeyProtected", "", s => s.OpenAiKey, (s, v) => s.OpenAiKey = (string)v!)
            .Is(SettingsPage.Privacy, ControlKind.Text, keys: ["openAiKey"]),
        // One setting under two keys: "restoreClipboard" is the old switch of the same choice (a view of it, written for older versions).
        new ExplicitField(SettingType.Choice, "clipboardAfterPaste", ClipboardAfterPaste.KeepDictation, s => s.ClipboardAfterPaste,
            (s, v) => s.ClipboardAfterPaste = (ClipboardAfterPaste)v!)
            .Is(SettingsPage.General, ControlKind.Choice, "Zwischenablage nach dem Einfügen", ["clipboard.after_paste", "clipboard.restore_after_paste"],
                s => Choices.AfterPaste.Label(s.ClipboardAfterPaste), ["restoreClipboard"]),
        // False until the user picks the meeting engine; its missing value comes from other stored keys.
        new ExplicitField(SettingType.Flag, "meetingSpeechEngineExplicit", false, s => s.MeetingSpeechEngineExplicit,
            (s, v) => s.MeetingSpeechEngineExplicit = (bool)v!)
            .Is(SettingsPage.Internal, ControlKind.None),
        // Written true by every save and read before everything else: the fresh install writes it, there is nothing to default.
        new ExplicitField(SettingType.Internal, "shortcutDefaultsAltV1")
            .Is(SettingsPage.Internal, ControlKind.None),
    ];

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
        ApplyStoredKey(dto.ApiKeyProtected, dto.ApiKeyPlain, openAi: false, protector, exact);
        ApplyStoredKey(dto.OpenAiKeyProtected, dto.OpenAiKeyPlain, openAi: true, protector, exact);
        return rewrite;
    }

    /// <summary>
    /// A failed decrypt leaves the key empty and keeps <paramref name="protectedText"/> so the next save writes
    /// that blob back. Null and a thrown exception both mean "cannot decrypt" (<see cref="ISecretProtector"/>).
    /// </summary>
    private void ApplyStoredKey(string? protectedText, string? plainText, bool openAi, ISecretProtector protector, bool exact)
    {
        if (!string.IsNullOrEmpty(protectedText))
        {
            string? plain = null;
            var failed = false;
            try { plain = protector.Unprotect(protectedText); }
            catch (Exception) { failed = true; }
            if (failed || plain == null)
            {
                KeepUndecryptable(openAi, protectedText);
                return;
            }
            if (openAi) OpenAiKey = plain;
            else ApiKey = plain;
            if (openAi) _retainedOpenAiKeyBlob = null;
            else _retainedApiKeyBlob = null;
            return;
        }
        if (!string.IsNullOrEmpty(plainText))
        {
            if (openAi) OpenAiKey = plainText;
            else ApiKey = plainText;
            return;
        }
        if (!exact) return;
        // The snapshot holds no key: drop a blob that belonged to the state we are leaving.
        if (openAi) { _openAiKey = ""; _retainedOpenAiKeyBlob = null; }
        else { _apiKey = ""; _retainedApiKeyBlob = null; }
    }

    /// <summary>
    /// Applies the explicit fields that need the table's values: the clipboard choice, the meeting-engine flag, the key clashes.
    /// Returns true when a retired Read Aloud model was moved, so the caller writes the file.
    /// </summary>
    private bool ApplyDerived(Dto dto)
    {
        // Missing "clipboardAfterPaste" = the old "restoreClipboard" switch decides; both missing = restore (older files did).
        ClipboardAfterPaste = Choices.AfterPasteFromFile(dto.ClipboardAfterPaste, dto.RestoreClipboard);
        // A file from before the flag always stored an engine, the factory default Gemini included, so "chosen" cannot be told
        // from "never chosen" there: Gemini reads as never chosen (follows dictation), any other value was a pick.
        MeetingSpeechEngineExplicit = dto.MeetingSpeechEngineExplicit
            ?? (dto.MeetingSpeechEngine != null && MeetingTranscription.Engine != Engine.Gemini);
        // An update adds the chat key to a file that never had it. It must not take a key the user already uses.
        if (dto.ChatHotkey == null && HotkeySlots.Clashes(this, HotkeyAction.Chat)) SetOn(HotkeyAction.Chat, false);
        // Same for the Read Aloud key (checked after the chat key has been placed).
        if (dto.ReadAloudHotkey == null && IsOn(HotkeyAction.ReadAloud) && HotkeySlots.Clashes(this, HotkeyAction.ReadAloud))
            SetOn(HotkeyAction.ReadAloud, false);
        // gpt-4o-mini-tts is retired (shutdown 2027-01-06). Move it once, at load. The save that
        // follows stores the replacement, so a later Gemini key does not move the user off the
        // Windows voice (Mac: retiredReadAloudReplacement, no fallback record).
        var replacement = ReadAloud.RetiredReplacement(ReadAloudModel, !string.IsNullOrWhiteSpace(ApiKey));
        if (replacement == null) return false;
        ReadAloudModel = replacement;
        return true;
    }

    /// <summary>Writes the explicit fields onto the Dto (the table writes the rest).</summary>
    private void WriteExplicit(Dto dto, Func<string, string> protect)
    {
        // Empty in memory with a retained blob: the user did not set a new key, so the stored ciphertext stays.
        dto.ApiKeyProtected = ApiKey.Length == 0 ? (_retainedApiKeyBlob ?? "") : protect(ApiKey);
        dto.OpenAiKeyProtected = OpenAiKey.Length == 0 ? (_retainedOpenAiKeyBlob ?? "") : protect(OpenAiKey);
        // Every shortcut value is written out, so from now on a missing field means the new default.
        dto.ShortcutDefaultsAltV1 = true;
        dto.RestoreClipboard = RestoreClipboard;
        dto.ClipboardAfterPaste = Choices.AfterPaste.Token(ClipboardAfterPaste);
        dto.MeetingSpeechEngineExplicit = MeetingSpeechEngineExplicit;
    }

    /// <summary>JSON keys of the Dto that no descriptor owns (a test pins this list to empty).</summary>
    internal static IEnumerable<string> UnownedJsonKeys => DtoByJson.Keys.Except(Descriptors.SelectMany(d => d.JsonKeys)).Order(StringComparer.Ordinal);

    /// <summary>Puts every row's new-install value on a new Settings (the constructor): the one place a default is written.</summary>
    private void ApplyDefaults()
    {
        foreach (var field in Fields) field.ApplyDefault(this);
        foreach (var field in ExplicitFields) field.ApplyDefault(this);
    }

    /// <summary>First row whose new-install value is not what a fresh Settings holds, or null.</summary>
    internal static string? NewInstallMismatch(Settings fresh) =>
        Fields.Concat(ExplicitFields).Select(f => f.CheckNewInstallDefault(fresh)).FirstOrDefault(m => m != null);
}
