using System.Globalization;
using System.Text.Json.Nodes;

namespace WhisperShortcut.Core.Mcp;

/// <summary>Kind of value a setting takes.</summary>
public enum McpSettingType
{
    /// <summary>true / false.</summary>
    Bool,
    /// <summary>One of a fixed list of options.</summary>
    Enum,
    /// <summary>A keyboard shortcut.</summary>
    Hotkey,
}

/// <summary>One choice of an enum setting.</summary>
/// <param name="Value">Stored value.</param>
/// <param name="Label">German label as the Settings window shows it.</param>
/// <param name="Available">False when it cannot be chosen right now.</param>
/// <param name="Reason">English reason for the AI app when not available.</param>
public sealed record McpSettingOption(string Value, string Label, bool Available, string? Reason);

/// <summary>A setting an AI app can read and (within the user's access level) change.</summary>
public sealed class McpSetting
{
    /// <summary>Stable id such as "shortcuts.dictate".</summary>
    public required string Id { get; init; }

    /// <summary>German title, shown to the user in the approval dialog and change log.</summary>
    public required string Title { get; init; }

    /// <summary>Category for filtering (models, shortcuts, read_aloud, clipboard, general).</summary>
    public required string Category { get; init; }

    /// <summary>Value kind.</summary>
    public required McpSettingType Type { get; init; }

    /// <summary>True when a change always waits for the user's approval.</summary>
    public bool ProposalOnly { get; init; }

    /// <summary>English hint for the AI app.</summary>
    public string? Description { get; init; }

    /// <summary>Current value as JSON (bool, string, or null when unknown).</summary>
    public required Func<JsonNode?> Read { get; init; }

    /// <summary>Options, for <see cref="McpSettingType.Enum"/> only; evaluated per call so availability is current.</summary>
    public Func<IReadOnlyList<McpSettingOption>>? Options { get; init; }

    /// <summary>Sets the value on Settings (or the host); the caller saves.</summary>
    public required Action<JsonNode?> Write { get; init; }

    /// <summary>Null = the value is acceptable; otherwise an English message.</summary>
    public string? Validate(JsonNode? value)
    {
        switch (Type)
        {
            case McpSettingType.Bool:
                return value is JsonValue b && b.TryGetValue<bool>(out _) ? null : "Must be true or false.";
            case McpSettingType.Enum:
            {
                if (value is not JsonValue s || !s.TryGetValue<string>(out var text)) return "Must be one of the option values (a string).";
                var options = Options?.Invoke() ?? Array.Empty<McpSettingOption>();
                var hit = options.FirstOrDefault(o => o.Value == text);
                if (hit == null) return $"Unknown option \"{text}\". Allowed: {string.Join(", ", options.Select(o => o.Value))}.";
                return hit.Available ? null : hit.Reason ?? "This option is not available.";
            }
            default:
            {
                if (!McpSettingsCatalog.TryHotkey(value, out var n))
                    return "Not a valid shortcut. " + McpSettingsCatalog.ShortcutFormatHint;
                return HotkeyRules.IsSafe(n) ? null : "Not a safe shortcut. " + McpSettingsCatalog.ShortcutFormatHint;
            }
        }
    }

    /// <summary>A hotkey becomes its normalized string; other values are returned unchanged (deep clone).</summary>
    public JsonNode? Normalize(JsonNode? value)
    {
        if (Type == McpSettingType.Hotkey && McpSettingsCatalog.TryHotkey(value, out var n)) return JsonValue.Create(n);
        return value?.DeepClone();
    }

    /// <summary>German label of a value: bool "An"/"Aus", the option label of an enum, the hotkey as "Strg+Umschalt+D".</summary>
    public string Label(JsonNode? value)
    {
        switch (Type)
        {
            case McpSettingType.Bool:
                return value is JsonValue b && b.TryGetValue<bool>(out var on) ? (on ? "An" : "Aus") : "Unbekannt";
            case McpSettingType.Enum:
            {
                var text = value is JsonValue s && s.TryGetValue<string>(out var t) ? t : null;
                var hit = Options?.Invoke().FirstOrDefault(o => o.Value == text);
                return hit?.Label ?? text ?? "Unbekannt";
            }
            default:
                if (value is JsonValue off && off.TryGetValue<string>(out var offText) && offText.Trim().Length == 0) return "Aus";
                return McpSettingsCatalog.TryHotkey(value, out var n) ? HotkeyRules.Display(n)
                    : value is JsonValue v && v.TryGetValue<string>(out var raw) ? raw : "Unbekannt";
        }
    }
}

/// <summary>
/// The settings AI apps may touch. Deliberately NOT in here: offline mode (switching it on turns this server off),
/// the microphone (device choice belongs to the user) and API keys (keys never go through AI apps).
/// </summary>
public static class McpSettingsCatalog
{
    /// <summary>How a shortcut value is written; sent to the AI app.</summary>
    public const string ShortcutFormatHint = "A string like \"Alt+1\" or \"Ctrl+Shift+D\": Alt plus a digit 0-9, or two of Ctrl/Alt/Shift (one of them Ctrl or Alt; Ctrl+Alt alone is AltGr on German keyboards and not allowed) plus a letter, digit or F1-F12. \"RightCtrl\" is the right Ctrl key alone (the Mac's rightControl). The object form {\"key\": \"1\", \"modifiers\": [\"alt\"]} is accepted too.";

    private const string NoSpeechModel = "The offline speech model is not downloaded. The user downloads it in WhisperShortcut Settings → Diktieren (speech) or Dictate Prompt (language model).";
    private const string NoLlm = "The offline language model is not downloaded. The user downloads it in WhisperShortcut Settings → Diktieren (speech) or Dictate Prompt (language model).";
    private const string NoGeminiKey = "No Gemini API key in WhisperShortcut. The user enters it in Settings → Allgemein; AI apps cannot set keys.";
    private const string NoOpenAiKey = "No OpenAI API key in WhisperShortcut. The user enters it in Settings → Allgemein; AI apps cannot set keys.";

    private static string SpeedToken(double speed) =>
        ReadAloud.NormalizeSpeed(speed).ToString("0.0#", CultureInfo.InvariantCulture);

    private static McpSetting VoiceSetting(string id, string title, TtsProvider provider, Func<string> get, Action<string> set) => new()
    {
        Id = id, Title = title, Category = "read_aloud", Type = McpSettingType.Enum,
        Description = $"The {(provider == TtsProvider.Gemini ? "Gemini" : "OpenAI")} voice Read Aloud speaks with. Only used when a model of that provider is selected.",
        Read = () => JsonValue.Create(get()),
        Options = () => ReadAloud.Voices(provider).Select(v => new McpSettingOption(v.Id, v.DisplayName, true, null)).ToList(),
        Write = v => set(ReadAloud.NormalizeVoice(provider, Text(v))),
    };

    /// <summary>String or macOS object form {key, modifiers} to the normalized hotkey string.</summary>
    internal static bool TryHotkey(JsonNode? value, out string normalized)
    {
        normalized = "";
        if (value is JsonValue v)
            return v.TryGetValue<string>(out var s) && HotkeyRules.TryNormalize(s, out normalized);
        if (value is JsonObject o && o["key"] is JsonValue kv && kv.TryGetValue<string>(out var key) && o["modifiers"] is JsonArray mods)
        {
            var parts = new List<string>();
            foreach (var m in mods)
            {
                if (m is not JsonValue mv || !mv.TryGetValue<string>(out var name)) return false;
                parts.Add(name.Trim().ToLowerInvariant() switch
                {
                    "control" or "ctrl" => "Ctrl",
                    "option" or "alt" => "Alt",
                    "shift" => "Shift",
                    "win" or "windows" or "command" => "Win",
                    _ => "?",
                });
            }
            parts.Add(key.Trim());
            return HotkeyRules.TryNormalize(string.Join("+", parts), out normalized);
        }
        return false;
    }

    private static string EngineName(Engine e) => e switch { Engine.Local => "local", Engine.OpenAI => "openai", _ => "gemini" };

    private static Engine ParseEngine(JsonNode? v) => (v as JsonValue)?.GetValue<string>() switch
    {
        "local" => Engine.Local,
        "openai" => Engine.OpenAI,
        _ => Engine.Gemini,
    };

    private static string Text(JsonNode? v) => v is JsonValue s && s.TryGetValue<string>(out var t) ? t : "";

    private static IReadOnlyList<McpSettingOption> ModelOptions(IReadOnlyList<ModelInfo> models) =>
        models.Select(m => new McpSettingOption(m.Id, m.Name, true, null)).ToList();

    private static McpSetting ModelSetting(string id, string title, IReadOnlyList<ModelInfo> models, Func<string> get, Action<string> set, string? description = null) => new()
    {
        Id = id, Title = title, Category = "models", Type = McpSettingType.Enum, Description = description,
        Read = () => JsonValue.Create(get()),
        Options = () => ModelOptions(models),
        Write = v => set(Text(v)),
    };

    private static McpSetting BoolSetting(string id, string title, string category, Func<bool> get, Action<bool> set) => new()
    {
        Id = id, Title = title, Category = category, Type = McpSettingType.Bool,
        Read = () => JsonValue.Create(get()),
        Write = v => set(v is JsonValue b && b.TryGetValue<bool>(out var on) && on),
    };

    // enabled/setEnabled: shortcuts with an on switch. Off reads as "" (the stored key is only a suggestion);
    // Write("") switches off (needed by undo and the applier's rollback), a real key switches on.
    private static McpSetting HotkeySetting(string id, string title, Func<string> get, Action<string> set,
        Func<bool>? enabled = null, Action<bool>? setEnabled = null) => new()
    {
        Id = id, Title = title, Category = "shortcuts", Type = McpSettingType.Hotkey,
        Read = () => enabled != null && !enabled()
            ? JsonValue.Create("")
            : JsonValue.Create(HotkeyRules.TryNormalize(get(), out var n) ? n : get()),
        Write = v =>
        {
            if (setEnabled != null && v is JsonValue j && j.TryGetValue<string>(out var text) && text.Trim().Length == 0)
            {
                setEnabled(false);
                return;
            }
            if (!TryHotkey(v, out var n)) throw new McpConfigException("Not a valid shortcut.");
            set(n);
            setEnabled?.Invoke(true);
        },
    };

    /// <summary>Builds the catalog over live <paramref name="settings"/>; options are evaluated on every call.</summary>
    public static IReadOnlyList<McpSetting> Build(Settings settings, IMcpHost host)
    {
        McpSettingOption Cloud(Engine e) => e == Engine.Gemini
            ? new McpSettingOption("gemini", "Google Gemini", !string.IsNullOrWhiteSpace(settings.ApiKey), string.IsNullOrWhiteSpace(settings.ApiKey) ? NoGeminiKey : null)
            : new McpSettingOption("openai", "OpenAI", !string.IsNullOrWhiteSpace(settings.OpenAiKey), string.IsNullOrWhiteSpace(settings.OpenAiKey) ? NoOpenAiKey : null);

        return new McpSetting[]
        {
            new()
            {
                Id = "dictation.engine", Title = "Spracherkennung (Anbieter)", Category = "models", Type = McpSettingType.Enum,
                Description = "Where speech recognition runs. Cloud providers send the audio to that provider.",
                Read = () => JsonValue.Create(EngineName(settings.SpeechEngine)),
                Options = () => new[]
                {
                    new McpSettingOption("local", "Auf diesem PC (Parakeet)", host.SpeechModelInstalled, host.SpeechModelInstalled ? null : NoSpeechModel),
                    Cloud(Engine.Gemini), Cloud(Engine.OpenAI),
                },
                Write = v => settings.SpeechEngine = ParseEngine(v),
            },
            new()
            {
                Id = "dictate_prompt.engine", Title = "Texte bearbeiten (Anbieter)", Category = "models", Type = McpSettingType.Enum,
                Description = "Where Dictate Prompt (editing and composing text) runs. Cloud providers send the text to that provider.",
                Read = () => JsonValue.Create(EngineName(settings.TextEngine)),
                Options = () =>
                {
                    var llm = host.LocalLlmInstalled(OfflineCatalog.LlmById(settings.OfflineLlmId).Id);
                    return new[]
                    {
                        new McpSettingOption("local", "Auf diesem PC (Sprachmodell)", llm, llm ? null : NoLlm),
                        Cloud(Engine.Gemini), Cloud(Engine.OpenAI),
                    };
                },
                Write = v => settings.TextEngine = ParseEngine(v),
            },
            ModelSetting("dictation.transcription_model", "Gemini-Modell Diktieren", GeminiModels.Transcription, () => settings.TranscriptionModel, v => settings.TranscriptionModel = v),
            ModelSetting("dictation.openai_transcription_model", "OpenAI-Modell Diktieren", OpenAiModels.Transcription, () => settings.OpenAiTranscriptionModel, v => settings.OpenAiTranscriptionModel = v),
            new()
            {
                Id = "dictation.punctuation", Title = "Satzzeichen (Offline-Spracherkennung)", Category = "models", Type = McpSettingType.Enum,
                Description = "Offline speech recognition only (Parakeet on this PC). automatic: the model sets punctuation. spokenOnly: only spoken marks (Punkt, Komma, Fragezeichen, Ausrufezeichen, Doppelpunkt, Schrägstrich) are written and no final period is added. Cloud models and meetings always use automatic.",
                Read = () => JsonValue.Create(settings.PunctuationMode == PunctuationMode.SpokenOnly ? "spokenOnly" : "automatic"),
                Options = () => new[]
                {
                    new McpSettingOption("automatic", "Automatisch", true, null),
                    new McpSettingOption("spokenOnly", "Nur wenn gesprochen", true, null),
                },
                Write = v => settings.PunctuationMode = Text(v) == "spokenOnly" ? PunctuationMode.SpokenOnly : PunctuationMode.Automatic,
            },
            new()
            {
                Id = "dictation.language", Title = "Sprache (OpenAI-Transkription)", Category = "models", Type = McpSettingType.Enum,
                Description = "Spoken language for OpenAI transcription (ISO 639-1). auto lets the model detect it. Gemini and offline Parakeet ignore it.",
                Read = () => JsonValue.Create(TranscriptionLanguages.Normalize(settings.TranscriptionLanguage)),
                Options = () => TranscriptionLanguages.All.Select(l => new McpSettingOption(l.Code, l.Name, true, null)).ToList(),
                Write = v => settings.TranscriptionLanguage = TranscriptionLanguages.Normalize(Text(v)),
            },
            new()
            {
                Id = "dictation.temperature", Title = "Temperatur (Gemini-Transkription)", Category = "models", Type = McpSettingType.Enum,
                Description = "Sampling temperature for Gemini transcription. 0.0 (default) reproduces what was said; higher values are the most likely source of invented or swapped words. OpenAI and Parakeet ignore it.",
                Read = () => JsonValue.Create(TranscriptionTuning.Format(settings.TranscriptionTemperature)),
                Options = () => TranscriptionTuning.Temperatures.Select(t => new McpSettingOption(TranscriptionTuning.Format(t.Value), t.Label, true, null)).ToList(),
                Write = v => settings.TranscriptionTemperature = double.TryParse(Text(v), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t) ? TranscriptionTuning.NormalizeTemperature(t)
                    : throw new McpConfigException("Use 0.0, 0.2, 0.5 or 1.0."),
            },
            new()
            {
                Id = "dictation.thinking", Title = "Denkaufwand (Gemini-Transkription)", Category = "models", Type = McpSettingType.Enum,
                Description = "Gemini thinking level before transcribing: minimal (default), low, medium, high. Models that reject minimal (3.7/3.8 Flash, Pro) run at low. OpenAI and Parakeet ignore it.",
                Read = () => JsonValue.Create(TranscriptionTuning.NormalizeThinking(settings.TranscriptionThinking)),
                Options = () => TranscriptionTuning.ThinkingLevels.Select(t => new McpSettingOption(t.Value, t.Label, true, null)).ToList(),
                Write = v => settings.TranscriptionThinking = TranscriptionTuning.NormalizeThinking(Text(v)),
            },
            ModelSetting("dictate_prompt.model", "Gemini-Modell Text bearbeiten", GeminiModels.Prompt, () => settings.PromptModel, v => settings.PromptModel = v),
            ModelSetting("dictate_prompt.openai_model", "OpenAI-Modell Text bearbeiten", OpenAiModels.Prompt, () => settings.OpenAiPromptModel, v => settings.OpenAiPromptModel = v),
            new()
            {
                Id = "offline.llm", Title = "Offline-Sprachmodell", Category = "models", Type = McpSettingType.Enum,
                Description = "The language model used for Dictate Prompt on this PC. If it is not downloaded yet, the user downloads it in WhisperShortcut Settings → Dictate Prompt.",
                Read = () => JsonValue.Create(OfflineCatalog.LlmById(settings.OfflineLlmId).Id),
                Options = () => OfflineCatalog.Llms.Select(l => new McpSettingOption(l.Id, l.DisplayName, true, null)).ToList(),
                Write = v => settings.OfflineLlmId = Text(v),
            },
            HotkeySetting("shortcuts.dictate", "Tastenkürzel Diktieren", () => settings.DictateHotkey, v => settings.DictateHotkey = v),
            HotkeySetting("shortcuts.dictate_prompt", "Tastenkürzel Text bearbeiten", () => settings.PromptHotkey, v => settings.PromptHotkey = v),
            HotkeySetting("shortcuts.add_to_glossary", "Tastenkürzel Glossar", () => settings.GlossaryHotkey, v => settings.GlossaryHotkey = v,
                () => settings.GlossaryHotkeyEnabled, v => settings.GlossaryHotkeyEnabled = v),
            HotkeySetting("shortcuts.read_aloud", "Tastenkürzel Vorlesen", () => settings.ReadAloudHotkey, v => settings.ReadAloudHotkey = v,
                () => settings.ReadAloudHotkeyEnabled, v => settings.ReadAloudHotkeyEnabled = v),
            new()
            {
                Id = "read_aloud.model", Title = "Vorlesen: Modell", Category = "models", Type = McpSettingType.Enum,
                Description = "The text-to-speech model Read Aloud uses. The selected text is sent to that provider.",
                Read = () => JsonValue.Create(ReadAloud.NormalizeModel(settings.ReadAloudModel)),
                Options = () => ReadAloud.Models.Select(m =>
                {
                    var needsKey = m.Provider == TtsProvider.OpenAI && string.IsNullOrWhiteSpace(settings.OpenAiKey);
                    return new McpSettingOption(m.Id, m.Name, !needsKey, needsKey ? NoOpenAiKey : null);
                }).ToList(),
                Write = v => settings.ReadAloudModel = ReadAloud.NormalizeModel(Text(v)),
            },
            VoiceSetting("read_aloud.voice.gemini", "Vorlesen: Gemini-Stimme", TtsProvider.Gemini,
                () => settings.ReadAloudVoiceFor(TtsProvider.Gemini), v => settings.ReadAloudVoiceGemini = v),
            VoiceSetting("read_aloud.voice.openai", "Vorlesen: OpenAI-Stimme", TtsProvider.OpenAI,
                () => settings.ReadAloudVoiceFor(TtsProvider.OpenAI), v => settings.ReadAloudVoiceOpenAi = v),
            new()
            {
                Id = "read_aloud.speed", Title = "Vorlesen: Geschwindigkeit", Category = "read_aloud", Type = McpSettingType.Enum,
                Description = "Playback speed of Read Aloud as a number string. The pitch of the voice stays the same.",
                Read = () => JsonValue.Create(SpeedToken(settings.ReadAloudSpeed)),
                Options = () => ReadAloud.Speeds.Select(x => new McpSettingOption(SpeedToken(x), ReadAloud.SpeedLabel(x), true, null)).ToList(),
                Write = v => settings.ReadAloudSpeed = double.TryParse(Text(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    ? ReadAloud.NormalizeSpeed(x) : ReadAloud.DefaultSpeed,
            },
            BoolSetting("read_aloud.smart_rewrite", "Vorlesen: intelligentes Umschreiben", "read_aloud",
                () => settings.ReadAloudSmartRewrite, v => settings.ReadAloudSmartRewrite = v),
            BoolSetting("clipboard.auto_paste", "Automatisch einfügen", "clipboard", () => settings.AutoPaste, v => settings.AutoPaste = v),
            new()
            {
                Id = "clipboard.after_paste", Title = "Zwischenablage nach dem Einfügen", Category = "clipboard", Type = McpSettingType.Enum,
                Description = "What happens to the clipboard after auto-paste (no effect while clipboard.auto_paste is off). keep: the dictated text stays (default). restore: the previous clipboard comes back 3 s after pasting. clear: the clipboard is emptied 3 s after pasting, if it still holds the dictation.",
                Read = () => JsonValue.Create(ClipboardAfterPasteModes.Name(settings.ClipboardAfterPaste)),
                Options = () => ClipboardAfterPasteModes.All
                    .Select(m => new McpSettingOption(ClipboardAfterPasteModes.Name(m), ClipboardAfterPasteModes.Label(m), true, null)).ToList(),
                Write = v => settings.ClipboardAfterPaste = ClipboardAfterPasteModes.Parse(Text(v))
                    ?? throw new McpConfigException("Use keep, restore or clear."),
            },
            BoolSetting("clipboard.restore_after_paste", "Zwischenablage wiederherstellen (alt, siehe clipboard.after_paste)", "clipboard", () => settings.RestoreClipboard, v => settings.RestoreClipboard = v),
            BoolSetting("general.sounds", "Signaltöne", "general", () => settings.Sounds, v => settings.Sounds = v),
            BoolSetting("general.auto_update_check", "Automatisch nach Updates suchen", "general", () => settings.AutoUpdateCheck, v => settings.AutoUpdateCheck = v),
            new()
            {
                Id = "general.launch_at_login", Title = "Mit Windows starten", Category = "general", Type = McpSettingType.Bool,
                ProposalOnly = true,
                Description = "Start WhisperShortcut when the user signs in to Windows. Always waits for the user's approval.",
                Read = () => host.AutoStartEnabled is { } on ? JsonValue.Create(on) : null,
                Write = v =>
                {
                    var on = v is JsonValue b && b.TryGetValue<bool>(out var x) && x;
                    if (!host.SetAutoStart(on)) throw new McpConfigException("Der automatische Start konnte nicht geändert werden.");
                },
            },
        };
    }

    private static JsonNode? Canonical(JsonNode? v)
    {
        if (v is JsonObject && TryHotkey(v, out var n)) return JsonValue.Create(n);
        if (v is JsonValue s && s.TryGetValue<string>(out var text) && HotkeyRules.TryNormalize(text, out var h)) return JsonValue.Create(h);
        return v;
    }

    /// <summary>Equal values (a hotkey in any accepted spelling equals its normalized form).</summary>
    public static bool SameValue(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Canonical(a), Canonical(b));

    /// <summary>Validates the state the whole batch would leave: the hotkeys must stay distinct (also against the unchanged ones). Null = ok.</summary>
    public static string? ValidateBatch(IReadOnlyList<McpSetting> catalog, IReadOnlyList<(string Id, JsonNode? Value)> items)
    {
        var seen = new Dictionary<string, McpSetting>();
        foreach (var s in catalog.Where(c => c.Type == McpSettingType.Hotkey))
        {
            var value = items.Where(i => i.Id == s.Id).Select(i => i.Value).FirstOrDefault(_ => true);
            var overridden = items.Any(i => i.Id == s.Id);
            if (!TryHotkey(overridden ? value : s.Read(), out var key)) continue;
            if (seen.TryGetValue(key, out var other))
                return $"{other.Title} and {s.Title} would both be {HotkeyRules.Display(key)}. Each shortcut must be different.";
            seen[key] = s;
        }
        return null;
    }
}
