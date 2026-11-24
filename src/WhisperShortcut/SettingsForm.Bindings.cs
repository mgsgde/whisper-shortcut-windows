namespace WhisperShortcut;

/// <summary>
/// Every value the user can change in this window, bound to its control in one list (<see cref="BuildBindings"/>). The window
/// saves only the bindings that were edited (<see cref="TrySave"/>), so a setting changed from outside while the window is open is
/// never overwritten by a stale control, and <see cref="Pull()"/> shows such a change in every control the user did not touch.
/// A new setting is its descriptor row in Core (Settings.Fields.cs) plus one line here.
/// </summary>
internal sealed partial class SettingsForm
{
    private Binding<string> _geminiKeyBinding = null!, _openAiKeyBinding = null!;
    private Binding<bool> _autoStartBinding = null!;

    /// <summary>True while a control differs from what was loaded or last saved: the moment between an edit and its automatic apply.</summary>
    public bool HasUnsavedChanges => _loaded && _bindings.Any(b => b.Edited);

    /// <summary>
    /// The settings were changed from outside (tray menu, playback bar, glossary key, AI app, chat): show the new values in every control the
    /// user did not edit. An edited control keeps its edit (and is written on the next save); nothing else is lost either way.
    /// </summary>
    public void Pull() => Pull(_settings);

    /// <summary><see cref="Pull()"/> against <paramref name="settings"/>.</summary>
    public void Pull(Core.Settings settings)
    {
        if (!_loaded || IsDisposed) return;
        foreach (var b in _bindings) b.Pull(settings);
    }

    private bool PollBindings(out bool moved)
    {
        bool edited = false;
        moved = false;
        foreach (var b in _bindings)
        {
            if (b.Poll(out var m)) edited = true;
            moved |= m;
        }
        return edited;
    }

    /// <summary>
    /// One setting of the window. <paramref name="key"/> is its descriptor (<see cref="Core.Settings.Descriptors"/>): an unknown key
    /// throws when the window is built, so a control cannot exist without its descriptor. <paramref name="get"/> / <paramref name="set"/>
    /// are the descriptor's own unless the control needs another view of the value.
    /// </summary>
    private Binding<T> Bind<T>(string key, Func<T> read, Action<T> show, Func<Core.Settings, T>? get = null, Action<Core.Settings, T>? set = null,
        IEqualityComparer<T>? eq = null, Func<T, T, T, T>? merge = null, Func<bool>? gate = null, bool gateApply = true)
    {
        var d = Core.Settings.Descriptor(key);
        if (d.Control == Core.ControlKind.None) throw new InvalidOperationException($"{key}: the descriptor says this setting has no control");
        return Add(new Binding<T>(get ?? (s => d.Get<T>(s)), set ?? ((s, v) => d.Set(s, v)), read, show, eq, merge, gate, gateApply) { Key = key });
    }

    /// <summary>A control for something that is not in settings.json (the Windows autostart lives in the registry).</summary>
    private Binding<T> BindExternal<T>(Func<Core.Settings, T> get, Func<T> read, Action<T> show) =>
        Add(new Binding<T>(get, (_, _) => { }, read, show));

    private Binding<T> Add<T>(Binding<T> binding)
    {
        _bindings.Add(binding);
        return binding;
    }

    /// <summary>The descriptor keys of the window's bindings (a binding without one is <see cref="BindExternal{T}"/>).</summary>
    internal IReadOnlyList<string> BindingKeysForTests => _bindings.Select(b => b.Key).OfType<string>().ToList();

    /// <summary>Bindings that are not a persisted setting.</summary>
    internal int ExternalBindingCountForTests => _bindings.Count(b => b.Key == null);

    private Binding<bool> BindToggle(ToggleSwitch toggle, string key) =>
        Bind(key, () => toggle.Checked, v => toggle.Checked = v);

    /// <summary>A combo filled with <see cref="ChoiceItem{T}"/>s.</summary>
    private Binding<T> BindChoice<T>(FluentCombo combo, string key, Func<Core.Settings, T>? get = null) =>
        Bind(key, () => ((ChoiceItem<T>)combo.SelectedItem!).Value, v => ShowChoice(combo, v), get);

    /// <summary>One model step (provider and its models) is one value.</summary>
    private Binding<Core.ModelSelection> BindStep(ModelStepControls step, string key,
        Func<Core.Settings, Core.ModelSelection>? get = null, Action<Core.Settings, Core.ModelSelection>? set = null) =>
        Bind(key, () => step.Selected, step.Show, get, set);

    private static void ShowChoice<T>(FluentCombo combo, T value)
    {
        if (combo.Items.Cast<ChoiceItem<T>>().FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, value)) is { } item)
            combo.SelectedItem = item;
    }

    private static IEqualityComparer<IReadOnlyList<T>> ListOf<T>(IEqualityComparer<T> item) =>
        EqualityComparer<IReadOnlyList<T>>.Create((a, b) => a!.SequenceEqual(b!, item));

    private static readonly IEqualityComparer<IReadOnlyList<string>> Strings = ListOf(EqualityComparer<string>.Default);

    private static readonly IEqualityComparer<IReadOnlyList<Core.DictationTemplate>> TemplateLists = ListOf(
        EqualityComparer<Core.DictationTemplate>.Create((a, b) =>
            a!.Id == b!.Id && a.Name == b.Name && a.Instructions == b.Instructions && a.Hotkey == b.Hotkey));

    private static IReadOnlyList<Core.DictationTemplate> CloneAll(IEnumerable<Core.DictationTemplate> templates) =>
        templates.Select(t => t.Clone()).ToList();

    private static readonly string[] LineBreaks = { "\r\n", "\n", "\r" };

    /// <summary>
    /// The glossary is edited in a text box and grows from outside (the glossary key): a term added meanwhile is appended to the
    /// text, edited or not, and never lost. (value = what the box shows, known = what the settings held, current = what they hold now.)
    /// </summary>
    private static string MergeGlossary(string value, string known, string current)
    {
        var old = known.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var lines = value.Split(LineBreaks, StringSplitOptions.None).ToList();
        foreach (var term in current.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (old.Contains(term) || lines.Any(l => string.Equals(l.Trim(), term, StringComparison.CurrentCultureIgnoreCase))) continue;
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(term);
        }
        return string.Join("\r\n", lines);
    }

    private void ShowMicrophone(string name)
    {
        var item = _mic.Items.Cast<MicItem>().FirstOrDefault(i => i.Value == name);
        if (item == null)
        {
            // Headset unplugged: keep the choice instead of silently switching to the default (as FillMicrophones).
            item = new MicItem(name, $"{name} (nicht angeschlossen, solange gilt Windows-Standard)");
            _mic.Items.Add(item);
        }
        _mic.SelectedItem = item;
    }

    private void ShowTemplates(IReadOnlyList<Core.DictationTemplate> value)
    {
        var currentId = CurrentTemplate?.Id;
        _templates.Clear();
        _templates.AddRange(CloneAll(value));
        FillTemplateChoice(Math.Max(0, _templates.FindIndex(t => t.Id == currentId)));
    }

    private void ShowReadAloudVoice(Core.TtsProvider provider, string voice)
    {
        switch (provider)
        {
            case Core.TtsProvider.Gemini: _readAloudVoiceGemini = voice; break;
            case Core.TtsProvider.OpenAI: _readAloudVoiceOpenAi = voice; break;
            default: _readAloudVoiceSystem = voice; break;
        }
        if (_readAloudVoiceProvider == provider) FillReadAloudVoices();
    }

    /// <summary>The one list of what the window saves: setting (by descriptor key), control, and how a value goes from one to the other.</summary>
    private void BuildBindings()
    {
        // ---- Datenschutz, Konten, Mikrofon
        Bind("offlineMode", () => _offlineMode.Checked, v => (v ? _offlineMode : _onlineMode).Checked = true);
        Bind("offlineLlmId", () => SelectedLlmChoice.Id,
            v => _llmChoice.SelectedItem = _llmChoice.Items.Cast<LlmItem>().First(i => i.Llm.Id == Core.OfflineCatalog.LlmById(v).Id));
        _geminiKeyBinding = Bind("apiKeyProtected", () => _geminiKey.Value, v => _geminiKey.Box.Text = v);
        _openAiKeyBinding = Bind("openAiKeyProtected", () => _openAiKey.Value, v => _openAiKey.Box.Text = v);
        Bind("microphone", () => SelectedMicrophone, ShowMicrophone);
        // Windows autostart lives in the registry, not in the settings: the binding only tells that the switch was edited (TrySave acts).
        _autoStartBinding = BindExternal(_ => AutoStart.IsEnabled, () => _autoStart.Checked, v => _autoStart.Checked = v);

        // ---- Allgemein: shortcuts. All or none: with a shortcut refused (invalid, taken twice) these keep their edit.
        foreach (var slot in Core.HotkeySlots.All)
        {
            if (BoxOf(slot) is not { } box) continue;
            var key = slot;
            Bind(key.JsonKey, () => box.Value, v => box.SetValue(v, notify: true), get: s => key.Active(s), set: (s, v) => StoreKey(s, key, v),
                gate: () => _keysOk);
        }
        BindToggle(_rightCtrl, "rightCtrlDictation");
        BindChoice<Core.UpdateChannel>(_updateChannel, "updateChannel");
        BindToggle(_autoUpdate, "autoUpdateCheck");
        BindToggle(_autoPaste, "autoPaste");
        BindChoice<Core.ClipboardAfterPaste>(_afterPaste, "clipboardAfterPaste");
        BindToggle(_sounds, "sounds");

        // ---- Diktieren
        BindChoice<Core.DictateMode>(_dictateMode, "dictateMode");
        BindStep(_dictateStep, "speechEngine");
        BindChoice<Core.PunctuationMode>(_punctuation, "punctuationMode");
        BindChoice<string>(_language, "transcriptionLanguage", s => Core.TranscriptionLanguages.Normalize(s.TranscriptionLanguage));
        Bind("glossary", () => _glossary.Text, v => _glossary.Text = v, s => string.Join("\r\n", s.Glossary),
            (s, v) => s.Glossary = v.Split(LineBreaks, StringSplitOptions.None).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList(),
            merge: MergeGlossary);
        Bind("dictateSystemPrompt", () => _dictatePrompt.Value, v => { _dictatePrompt.Show(v); _openedPrompts.Dictate = v; });
        BindChoice<double>(_temperature, "transcriptionTemperature", s => Core.TranscriptionTuning.NormalizeTemperature(s.TranscriptionTemperature));
        BindChoice<string>(_thinking, "transcriptionThinking", s => Core.TranscriptionTuning.NormalizeThinking(s.TranscriptionThinking));

        // ---- Dictate Prompt
        BindStep(_promptStep, "textEngine");
        BindToggle(_sendScreenshots, "sendScreenshots");
        Bind<IReadOnlyList<string>>("quickActions", () => _quick.Select(q => q.Text).ToList(),
            v => { for (int i = 0; i < _quick.Length; i++) _quick[i].Text = v[i]; },
            s => Enumerable.Range(0, _quick.Length).Select(i => i < s.QuickActions.Count ? s.QuickActions[i] : "").ToList(),
            (s, v) => s.QuickActions = Core.QuickActionList.Clean(v), Strings);
        Bind("dictatePromptTemplate", () => _template.Text, v => { _template.Text = v; _openedPrompts.Template = v.ReplaceLineEndings("\n").Trim(); },
            s => (s.DictatePromptTemplate ?? "").ReplaceLineEndings("\r\n"),
            (s, v) => s.DictatePromptTemplate = v.Trim().Length == 0 ? "" : v.ReplaceLineEndings("\n").Trim());
        Bind("promptModeSystemPrompt", () => _promptPrompt.Value, v => { _promptPrompt.Show(v); _openedPrompts.PromptMode = v; });

        // ---- Vorlagen: names, instructions and the list are saved even with a refused shortcut; their keys wait (TemplateSave).
        Bind<IReadOnlyList<Core.DictationTemplate>>("templates", () => CloneAll(_templates), ShowTemplates,
            s => CloneAll(s.Templates), (s, v) => s.Templates = Core.TemplateSave.Build(v, s.Templates, _keysOk, Core.HotkeySlots.Normalize),
            TemplateLists, gate: () => _keysOk, gateApply: false);

        // ---- Vorlesen
        Bind("readAloudModel", () => SelectedModel(_readAloudModel),
            v => _readAloudModel.SelectedItem = _readAloudModel.Items.Cast<ModelItem>().FirstOrDefault(i => i.Model.Id == v) ?? _readAloudModel.SelectedItem);
        Bind("readAloudVoiceGemini", () => _readAloudVoiceGemini, v => ShowReadAloudVoice(Core.TtsProvider.Gemini, v),
            s => s.ReadAloudVoiceFor(Core.TtsProvider.Gemini));
        Bind("readAloudVoiceOpenAi", () => _readAloudVoiceOpenAi, v => ShowReadAloudVoice(Core.TtsProvider.OpenAI, v),
            s => s.ReadAloudVoiceFor(Core.TtsProvider.OpenAI));
        Bind("readAloudVoiceSystem", () => _readAloudVoiceSystem, v => ShowReadAloudVoice(Core.TtsProvider.System, v),
            s => s.ReadAloudVoiceFor(Core.TtsProvider.System));
        // The playback bar changes the speed while the window is open: it flows in through Pull, an untouched box never writes it back.
        Bind("readAloudSpeed", () => SelectedReadAloudSpeed, SelectReadAloudSpeed, s => Core.ReadAloud.NormalizeSpeed(s.ReadAloudSpeed));
        BindToggle(_readAloudRewrite, "readAloudSmartRewrite");
        Bind("readAloudRewritePrompt", () => _readAloudPrompt.Value, _readAloudPrompt.Show);

        // ---- Chat
        BindStep(_chatStep, "chatEngine");
        BindToggle(_chatWebSearch, "chatWebSearch");
        BindToggle(_chatCloseOnFocusLoss, "chatCloseOnFocusLoss");
        Bind("chatSystemPrompt", () => _chatPrompt.Value, _chatPrompt.Show);

        // ---- Meetings. Until the engine was picked here, meetings follow Diktieren; a different engine makes the pick explicit.
        Binding<Core.ModelSelection>? speech = null;
        speech = BindStep(_meetingSpeechStep, "meetingSpeechEngine", s => s.MeetingTranscription with { Engine = s.ConfiguredMeetingSpeechEngine }, (s, v) =>
        {
            if (v.Engine != speech!.Baseline.Engine) s.MeetingSpeechEngineExplicit = true;
            s.MeetingTranscription = s.MeetingSpeechEngineExplicit ? v : v with { Engine = s.MeetingTranscription.Engine };
        });
        BindStep(_meetingTextStep, "meetingTextEngine");
        Bind("meetingChunkSeconds", () => SelectedChoice(_meetingChunk), v => SelectChoice(_meetingChunk, v));
        Bind("meetingSafeguardMinutes", () => SelectedChoice(_meetingSafeguard), v => SelectChoice(_meetingSafeguard, v));
    }
}
