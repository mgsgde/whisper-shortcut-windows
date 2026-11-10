namespace WhisperShortcut;

/// <summary>
/// Every value the user can change in this window, bound to its control in one list (<see cref="BuildBindings"/>). The window
/// saves only the bindings that were edited (<see cref="TrySave"/>), so a setting changed from outside while the window is open is
/// never overwritten by a stale control, and <see cref="Pull()"/> shows such a change in every control the user did not touch.
/// A new setting is one line here (plus its field in Core.Settings).
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

    private Binding<T> Bind<T>(Func<Core.Settings, T> get, Action<Core.Settings, T> set, Func<T> read, Action<T> show,
        IEqualityComparer<T>? eq = null, Func<T, T, T, T>? merge = null, Func<bool>? gate = null, bool gateApply = true)
    {
        var binding = new Binding<T>(get, set, read, show, eq, merge, gate, gateApply);
        _bindings.Add(binding);
        return binding;
    }

    private Binding<bool> BindToggle(ToggleSwitch toggle, Func<Core.Settings, bool> get, Action<Core.Settings, bool> set) =>
        Bind(get, set, () => toggle.Checked, v => toggle.Checked = v);

    /// <summary>A combo filled with <see cref="ChoiceItem{T}"/>s.</summary>
    private Binding<T> BindChoice<T>(FluentCombo combo, Func<Core.Settings, T> get, Action<Core.Settings, T> set) =>
        Bind(get, set, () => ((ChoiceItem<T>)combo.SelectedItem!).Value, v => ShowChoice(combo, v));

    /// <summary>One model step (provider and its models) is one value.</summary>
    private Binding<Core.ModelSelection> BindStep(ModelStepControls step, Func<Core.Settings, Core.ModelSelection> get,
        Action<Core.Settings, Core.ModelSelection> set) =>
        Bind(get, set, () => step.Selected, step.Show);

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

    /// <summary>The one list of what the window saves: setting, control, and how a value goes from one to the other.</summary>
    private void BuildBindings()
    {
        // ---- Datenschutz, Konten, Mikrofon
        Bind(s => s.OfflineMode, (s, v) => s.OfflineMode = v, () => _offlineMode.Checked, v => (v ? _offlineMode : _onlineMode).Checked = true);
        Bind(s => s.OfflineLlmId, (s, v) => s.OfflineLlmId = v, () => SelectedLlmChoice.Id,
            v => _llmChoice.SelectedItem = _llmChoice.Items.Cast<LlmItem>().First(i => i.Llm.Id == Core.OfflineCatalog.LlmById(v).Id));
        _geminiKeyBinding = Bind(s => s.ApiKey, (s, v) => s.ApiKey = v, () => _geminiKey.Value, v => _geminiKey.Box.Text = v);
        _openAiKeyBinding = Bind(s => s.OpenAiKey, (s, v) => s.OpenAiKey = v, () => _openAiKey.Value, v => _openAiKey.Box.Text = v);
        Bind(s => s.Microphone, (s, v) => s.Microphone = v, () => SelectedMicrophone, ShowMicrophone);
        // Windows autostart lives in the registry, not in the settings: the binding only tells that the switch was edited (TrySave acts).
        _autoStartBinding = Bind(_ => AutoStart.IsEnabled, (_, _) => { }, () => _autoStart.Checked, v => _autoStart.Checked = v);

        // ---- Allgemein: shortcuts. All or none: with a shortcut refused (invalid, taken twice) these keep their edit.
        foreach (var slot in Core.HotkeySlots.All)
        {
            if (BoxOf(slot) is not { } box) continue;
            var key = slot;
            Bind(s => key.Active(s), (s, v) => StoreKey(s, key, v), () => box.Value, v => box.SetValue(v, notify: true), gate: () => _keysOk);
        }
        BindToggle(_rightCtrl, s => s.RightCtrlDictation, (s, v) => s.RightCtrlDictation = v);
        BindToggle(_autoUpdate, s => s.AutoUpdateCheck, (s, v) => s.AutoUpdateCheck = v);
        BindToggle(_autoPaste, s => s.AutoPaste, (s, v) => s.AutoPaste = v);
        BindChoice<Core.ClipboardAfterPaste>(_afterPaste, s => s.ClipboardAfterPaste, (s, v) => s.ClipboardAfterPaste = v);
        BindToggle(_sounds, s => s.Sounds, (s, v) => s.Sounds = v);

        // ---- Diktieren
        BindChoice<Core.DictateMode>(_dictateMode, s => s.DictateMode, (s, v) => s.DictateMode = v);
        BindStep(_dictateStep, s => s.Dictate, (s, v) => s.Dictate = v);
        BindChoice<Core.PunctuationMode>(_punctuation, s => s.PunctuationMode, (s, v) => s.PunctuationMode = v);
        BindChoice<string>(_language, s => Core.TranscriptionLanguages.Normalize(s.TranscriptionLanguage), (s, v) => s.TranscriptionLanguage = v);
        Bind(s => string.Join("\r\n", s.Glossary), (s, v) => s.Glossary = v.Split(LineBreaks, StringSplitOptions.None)
                .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList(),
            () => _glossary.Text, v => _glossary.Text = v, merge: MergeGlossary);
        Bind(s => s.DictateSystemPrompt, (s, v) => s.DictateSystemPrompt = v, () => _dictatePrompt.Value,
            v => { _dictatePrompt.Show(v); _openedPrompts.Dictate = v; });
        BindChoice<double>(_temperature, s => Core.TranscriptionTuning.NormalizeTemperature(s.TranscriptionTemperature), (s, v) => s.TranscriptionTemperature = v);
        BindChoice<string>(_thinking, s => Core.TranscriptionTuning.NormalizeThinking(s.TranscriptionThinking), (s, v) => s.TranscriptionThinking = v);

        // ---- Dictate Prompt
        BindStep(_promptStep, s => s.Prompt, (s, v) => s.Prompt = v);
        BindToggle(_sendScreenshots, s => s.SendScreenshots, (s, v) => s.SendScreenshots = v);
        Bind<IReadOnlyList<string>>(s => Enumerable.Range(0, _quick.Length).Select(i => i < s.QuickActions.Count ? s.QuickActions[i] : "").ToList(),
            (s, v) => s.QuickActions = Core.QuickActionList.Clean(v), () => _quick.Select(q => q.Text).ToList(),
            v => { for (int i = 0; i < _quick.Length; i++) _quick[i].Text = v[i]; }, Strings);
        Bind(s => (s.DictatePromptTemplate ?? "").ReplaceLineEndings("\r\n"),
            (s, v) => s.DictatePromptTemplate = v.Trim().Length == 0 ? "" : v.ReplaceLineEndings("\n").Trim(),
            () => _template.Text, v => { _template.Text = v; _openedPrompts.Template = v.ReplaceLineEndings("\n").Trim(); });
        Bind(s => s.PromptModeSystemPrompt, (s, v) => s.PromptModeSystemPrompt = v, () => _promptPrompt.Value,
            v => { _promptPrompt.Show(v); _openedPrompts.PromptMode = v; });

        // ---- Vorlagen: names, instructions and the list are saved even with a refused shortcut; their keys wait (TemplateSave).
        Bind<IReadOnlyList<Core.DictationTemplate>>(s => CloneAll(s.Templates),
            (s, v) => s.Templates = Core.TemplateSave.Build(v, s.Templates, _keysOk, Core.HotkeySlots.Normalize),
            () => CloneAll(_templates), ShowTemplates, TemplateLists, gate: () => _keysOk, gateApply: false);

        // ---- Vorlesen
        Bind(s => s.ReadAloudModel, (s, v) => s.ReadAloudModel = v, () => SelectedModel(_readAloudModel),
            v => _readAloudModel.SelectedItem = _readAloudModel.Items.Cast<ModelItem>().FirstOrDefault(i => i.Model.Id == v) ?? _readAloudModel.SelectedItem);
        Bind(s => s.ReadAloudVoiceFor(Core.TtsProvider.Gemini), (s, v) => s.ReadAloudVoiceGemini = v, () => _readAloudVoiceGemini,
            v => ShowReadAloudVoice(Core.TtsProvider.Gemini, v));
        Bind(s => s.ReadAloudVoiceFor(Core.TtsProvider.OpenAI), (s, v) => s.ReadAloudVoiceOpenAi = v, () => _readAloudVoiceOpenAi,
            v => ShowReadAloudVoice(Core.TtsProvider.OpenAI, v));
        Bind(s => s.ReadAloudVoiceFor(Core.TtsProvider.System), (s, v) => s.ReadAloudVoiceSystem = v, () => _readAloudVoiceSystem,
            v => ShowReadAloudVoice(Core.TtsProvider.System, v));
        // The playback bar changes the speed while the window is open: it flows in through Pull, an untouched box never writes it back.
        Bind(s => Core.ReadAloud.NormalizeSpeed(s.ReadAloudSpeed), (s, v) => s.ReadAloudSpeed = v, () => SelectedReadAloudSpeed, SelectReadAloudSpeed);
        BindToggle(_readAloudRewrite, s => s.ReadAloudSmartRewrite, (s, v) => s.ReadAloudSmartRewrite = v);
        Bind(s => s.ReadAloudRewritePrompt, (s, v) => s.ReadAloudRewritePrompt = v, () => _readAloudPrompt.Value, _readAloudPrompt.Show);

        // ---- Chat
        BindStep(_chatStep, s => s.Chat, (s, v) => s.Chat = v);
        BindToggle(_chatWebSearch, s => s.ChatWebSearch, (s, v) => s.ChatWebSearch = v);
        BindToggle(_chatCloseOnFocusLoss, s => s.ChatCloseOnFocusLoss, (s, v) => s.ChatCloseOnFocusLoss = v);
        Bind(s => s.ChatSystemPrompt, (s, v) => s.ChatSystemPrompt = v, () => _chatPrompt.Value, _chatPrompt.Show);

        // ---- Meetings. Until the engine was picked here, meetings follow Diktieren; a different engine makes the pick explicit.
        Binding<Core.ModelSelection>? speech = null;
        speech = BindStep(_meetingSpeechStep, s => s.MeetingTranscription with { Engine = s.ConfiguredMeetingSpeechEngine }, (s, v) =>
        {
            if (v.Engine != speech!.Baseline.Engine) s.MeetingSpeechEngineExplicit = true;
            s.MeetingTranscription = s.MeetingSpeechEngineExplicit ? v : v with { Engine = s.MeetingTranscription.Engine };
        });
        BindStep(_meetingTextStep, s => s.MeetingText, (s, v) => s.MeetingText = v);
        Bind(s => s.MeetingChunkSeconds, (s, v) => s.MeetingChunkSeconds = v, () => SelectedChoice(_meetingChunk), v => SelectChoice(_meetingChunk, v));
        Bind(s => s.MeetingSafeguardMinutes, (s, v) => s.MeetingSafeguardMinutes = v, () => SelectedChoice(_meetingSafeguard), v => SelectChoice(_meetingSafeguard, v));
    }
}
