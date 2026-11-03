namespace WhisperShortcut;

/// <summary>
/// Page „Vorlesen“ (Mac ReadAloudSettingsTab: voice model, voice, playback speed, smart rewriting, how to use) and the
/// Vorlesen shortcut row on Allgemein. Kept in its own file so the main settings file only gets the hook lines: the page
/// constant and nav entry, the shortcut row, <see cref="ReadAloudSnapshot"/> in Snapshot(), <see cref="ApplyReadAloud"/> /
/// <see cref="RestoreReadAloud"/> in TrySave() and <see cref="ReadAloudKeyProblem"/> in the shortcut check.
/// </summary>
internal sealed partial class SettingsForm
{
    public const string ReadAloudPage = "Vorlesen";

    private HotkeyBox _readAloudKey = null!;
    private FluentCombo _readAloudModel = null!, _readAloudVoice = null!, _readAloudSpeed = null!;
    private ToggleSwitch _readAloudRewrite = null!;
    // Each provider keeps its own voice (Mac: stored per provider), also while the model is switched back and forth here.
    private string _readAloudVoiceGemini = "", _readAloudVoiceOpenAi = "";
    private Core.TtsProvider _readAloudVoiceProvider;
    // The speed this window last showed or wrote: the playback card can change the setting while the window is open.
    private double _readAloudSpeedShown;

    private sealed record VoiceItem(Core.TtsVoice Voice)
    {
        public override string ToString() => Voice.DisplayName;
    }

    private sealed record SpeedItem(double Speed)
    {
        public override string ToString() => Core.ReadAloud.SpeedLabel(Speed) + (Speed == Core.ReadAloud.DefaultSpeed ? " (Empfohlen)" : "");
    }

    /// <summary>The „Vorlesen“ row of the shortcut list on Allgemein (Mac order: after Bildschirmfoto).</summary>
    private TableLayoutPanel ReadAloudKeyRow()
    {
        _readAloudKey = new HotkeyBox(_settings.ActiveReadAloudHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Vorlesen" };
        return KeyRow("Vorlesen", "Markierten Text vorlesen lassen. Ein zweiter Druck beendet es.", _readAloudKey);
    }

    private void BuildReadAloudPage()
    {
        SectionIcons.TryAdd("Sprachmodell", "\uE7F3");
        SectionIcons.TryAdd("Stimme", "\uE720");
        SectionIcons.TryAdd("Wiedergabetempo", "\uEC4A");
        SectionIcons.TryAdd("Intelligentes Umschreiben", "\uE70F");
        var page = NewPage(ReadAloudPage, "Markierten Text in jeder App vorlesen lassen: markieren, Kürzel drücken, zuhören.");

        // --- voice model (Mac TTSModelSelectionView) ---
        page.Controls.Add(Section("Sprachmodell", first: true, subtitle: "Welcher Anbieter die Sprache erzeugt. Die Stimme wählst du darunter."));
        _readAloudModel = ModelChoice(Core.ReadAloud.Models.Select(m => new Core.ModelInfo(m.Id, m.Name, m.Description)).ToList(),
            _settings.ReadAloudModel);
        page.Controls.Add(ModelCard(_readAloudModel, "Sprachmodell", "Gemini-Stimmen brauchen den Google-API-Schlüssel, GPT-4o mini TTS "
            + "den OpenAI-Schlüssel (Konten). Der markierte Text geht zum Vorlesen an diesen Anbieter."));

        // --- voice, per provider ---
        page.Controls.Add(Section("Stimme", subtitle: "Die Stimme, mit der vorgelesen wird. Jeder Anbieter hat eigene Stimmen."));
        _readAloudVoiceGemini = _settings.ReadAloudVoiceFor(Core.TtsProvider.Gemini);
        _readAloudVoiceOpenAi = _settings.ReadAloudVoiceFor(Core.TtsProvider.OpenAI);
        _readAloudVoice = new FluentCombo { Width = ComboWidth, AccessibleName = "Stimme" };
        _readAloudVoiceProvider = ReadAloudProvider;
        FillReadAloudVoices();
        _readAloudVoice.SelectedIndexChanged += (_, _) =>
        {
            if (_readAloudVoice.SelectedItem is not VoiceItem v) return;
            if (_readAloudVoiceProvider == Core.TtsProvider.Gemini) _readAloudVoiceGemini = v.Voice.Id;
            else _readAloudVoiceOpenAi = v.Voice.Id;
        };
        _readAloudModel.SelectedIndexChanged += (_, _) =>
        {
            if (ReadAloudProvider == _readAloudVoiceProvider) return;
            _readAloudVoiceProvider = ReadAloudProvider;
            FillReadAloudVoices();
        };
        var voiceCard = new Card();
        voiceCard.AddRow(SettingRow("Stimme", null, _readAloudVoice, ComboWidth, out _));
        page.Controls.Add(voiceCard);

        // --- playback speed ---
        page.Controls.Add(Section("Wiedergabetempo", subtitle: "Wie schnell vorgelesen wird. Die Tonhöhe bleibt gleich."));
        _readAloudSpeed = new FluentCombo { Width = ComboWidth, AccessibleName = "Wiedergabetempo" };
        foreach (var speed in Core.ReadAloud.Speeds) _readAloudSpeed.Items.Add(new SpeedItem(speed));
        _readAloudSpeedShown = _settings.ReadAloudSpeed;
        SelectReadAloudSpeed(_readAloudSpeedShown);
        var speedCard = new Card();
        speedCard.AddRow(SettingRow("Tempo", "Lässt sich beim Vorlesen auch in der Leiste unten umschalten.", _readAloudSpeed, ComboWidth, out _));
        page.Controls.Add(speedCard);

        // --- smart rewriting ---
        page.Controls.Add(Section("Intelligentes Umschreiben", subtitle: "Lass dein Textmodell (Dictate Prompt) Code, Logs, Markdown oder "
            + "unordentliche Notizen fürs Vorlesen umschreiben. Normaler Fließtext wird direkt vorgelesen, ohne den zusätzlichen Umweg."));
        _readAloudRewrite = new ToggleSwitch { Checked = _settings.ReadAloudSmartRewrite, AccessibleName = "Text vor dem Vorlesen sprechgerecht umschreiben" };
        var rewriteCard = new Card();
        rewriteCard.AddRow(SettingRow("Text vor dem Vorlesen sprechgerecht umschreiben", "Kostet vor dem Vorlesen einen kurzen Modellaufruf.",
            _readAloudRewrite, ToggleWidth, out _));
        page.Controls.Add(rewriteCard);

        // --- how to use ---
        page.Controls.Add(Section("So geht's"));
        var keyShown = new ThemedLabel { AutoSize = true, Font = Theme.Strong, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        void ShowKey() => keyShown.Text = Hotkey.TryParse(_readAloudKey.Value, out var k) ? k.ToDisplayString() : "Nicht festgelegt";
        _readAloudKey.ValueChanged += ShowKey;
        ShowKey();
        var changeKey = new LinkLabel { Text = "Ändern …", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Kürzel für Vorlesen ändern" };
        changeKey.LinkClicked += (_, _) => { ShowPage(General); _readAloudKey.Focus(); };
        var keyLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        keyLine.Controls.Add(keyShown);
        keyLine.Controls.Add(changeKey);
        var howCard = new Card();
        howCard.AddRow(SettingRow("Tastenkürzel", null, keyLine, ComboWidth, out _));
        var how = Block();
        how.Controls.Add(Wrap("Markiere einen beliebigen Text mit Maus oder Tastatur und drück dann das Tastenkürzel für Vorlesen.", 2 * RowPadding));
        how.Controls.Add(Wrap("Drück das Tastenkürzel noch einmal, um die Wiedergabe zu beenden. Unten in der Leiste kannst du anhalten, "
            + "10 Sekunden springen und das Tempo ändern.", 2 * RowPadding));
        how.Controls.Add(Wrap("Cloud-Stimmen brauchen einen API-Schlüssel (Konten). Im Offline-Modus ist Vorlesen aus, weil der Text dafür "
            + "an den Anbieter geht.", 2 * RowPadding, small: true));
        howCard.AddRow(how);
        page.Controls.Add(howCard);
    }

    private Core.TtsProvider ReadAloudProvider => Core.ReadAloud.ModelById(SelectedModel(_readAloudModel)).Provider;

    private void FillReadAloudVoices()
    {
        var provider = _readAloudVoiceProvider;
        var stored = provider == Core.TtsProvider.Gemini ? _readAloudVoiceGemini : _readAloudVoiceOpenAi;
        _readAloudVoice.BeginUpdate();
        _readAloudVoice.Items.Clear();
        foreach (var v in Core.ReadAloud.Voices(provider)) _readAloudVoice.Items.Add(new VoiceItem(v));
        _readAloudVoice.EndUpdate();
        var id = Core.ReadAloud.NormalizeVoice(provider, stored);
        _readAloudVoice.SelectedItem = _readAloudVoice.Items.Cast<VoiceItem>().First(i => i.Voice.Id == id);
    }

    private void SelectReadAloudSpeed(double speed) =>
        _readAloudSpeed.SelectedItem = _readAloudSpeed.Items.Cast<SpeedItem>().FirstOrDefault(i => i.Speed == Core.ReadAloud.NormalizeSpeed(speed))
            ?? _readAloudSpeed.Items[1];

    private double SelectedReadAloudSpeed => ((SpeedItem)_readAloudSpeed.SelectedItem!).Speed;

    /// <summary>The page's state for <c>Snapshot()</c>: equal = nothing to apply.</summary>
    private string ReadAloudSnapshot() => string.Join("\u0002", _readAloudKey.Value, SelectedModel(_readAloudModel),
        _readAloudVoiceGemini, _readAloudVoiceOpenAi, SelectedReadAloudSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _readAloudRewrite.Checked.ToString());

    private sealed record ReadAloudState(string Hotkey, bool HotkeyEnabled, string Model, string VoiceGemini, string VoiceOpenAi,
        double Speed, bool Rewrite);

    /// <summary>Writes the page into the settings (from TrySave); returns what was there, for <see cref="RestoreReadAloud"/>.</summary>
    /// <param name="keysOk">All shortcuts valid: only then the Vorlesen key is taken over, like the other keys.</param>
    private ReadAloudState ApplyReadAloud(bool keysOk)
    {
        var before = new ReadAloudState(_settings.ReadAloudHotkey, _settings.ReadAloudHotkeyEnabled, _settings.ReadAloudModel,
            _settings.ReadAloudVoiceGemini, _settings.ReadAloudVoiceOpenAi, _settings.ReadAloudSpeed, _settings.ReadAloudSmartRewrite);
        if (keysOk) (_settings.ReadAloudHotkey, _settings.ReadAloudHotkeyEnabled) = StoredKey(_readAloudKey.Value, _settings.ReadAloudHotkey);
        _settings.ReadAloudModel = SelectedModel(_readAloudModel);
        _settings.ReadAloudVoiceGemini = _readAloudVoiceGemini;
        _settings.ReadAloudVoiceOpenAi = _readAloudVoiceOpenAi;
        // Untouched here = keep what the playback card may have set meanwhile.
        if (SelectedReadAloudSpeed != _readAloudSpeedShown) _settings.ReadAloudSpeed = _readAloudSpeedShown = SelectedReadAloudSpeed;
        _settings.ReadAloudSmartRewrite = _readAloudRewrite.Checked;
        return before;
    }

    /// <summary>Saving failed: put the settings back as they were.</summary>
    private void RestoreReadAloud(ReadAloudState s)
    {
        _settings.ReadAloudHotkey = s.Hotkey;
        _settings.ReadAloudHotkeyEnabled = s.HotkeyEnabled;
        _settings.ReadAloudModel = s.Model;
        _settings.ReadAloudVoiceGemini = s.VoiceGemini;
        _settings.ReadAloudVoiceOpenAi = s.VoiceOpenAi;
        _settings.ReadAloudSpeed = s.Speed;
        _settings.ReadAloudSmartRewrite = s.Rewrite;
    }

    /// <summary>The Vorlesen key: valid and not taken by a key checked before it; then it is taken, too.</summary>
    private string? ReadAloudKeyProblem(List<(string Owner, Hotkey Key)> taken, string rule)
    {
        if (_readAloudKey.Value.Length == 0) return null;
        if (!Hotkey.TryParse(_readAloudKey.Value, out var key) || !IsSafe(key)) return "Kürzel für Vorlesen: " + rule;
        if (taken.FirstOrDefault(k => k.Key == key) is { Owner: { } owner })
            return $"{key.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für Vorlesen ein anderes Kürzel wählen.";
        taken.Add(("Vorlesen", key));
        return null;
    }
}
