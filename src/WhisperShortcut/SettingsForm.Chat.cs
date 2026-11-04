namespace WhisperShortcut;

/// <summary>
/// Page „Chat“ (Mac ChatSettingsTab: chat model, web search, system prompt, how to use, under „Erweitert“ the window behaviour)
/// and the Chat row of the shortcut list on Allgemein. Own file: the main settings file only gets the hook lines (nav entry,
/// the shortcut row, <see cref="BuildChatPage"/>, <see cref="ChatSnapshot"/>, <see cref="ApplyChat"/> / <see cref="RestoreChat"/>,
/// <see cref="ChatKeyProblem"/>).
/// </summary>
internal sealed partial class SettingsForm
{
    public const string ChatPage = "Chat";

    private HotkeyBox _chatKey = null!;
    private FluentCombo _chatEngine = null!, _chatModel = null!, _chatModelOpenAi = null!;
    private ToggleSwitch _chatWebSearch = null!, _chatCloseOnFocusLoss = null!;
    private PromptEditor _chatPrompt = null!;

    /// <summary>The „Chat“ row of the shortcut list on Allgemein (Mac: Chat öffnen/schließen ⌥Space).</summary>
    private TableLayoutPanel ChatKeyRow()
    {
        _chatKey = new HotkeyBox(_settings.ActiveChatHotkey, allowEmpty: true) { AccessibleName = "Tastenkürzel Chat" };
        return KeyRow("Chat öffnen/schließen", "Öffnet das Chatfenster, ein zweiter Druck schließt es.", _chatKey);
    }

    private void BuildChatPage()
    {
        SectionIcons.TryAdd("Chat-Modell", "");
        SectionIcons.TryAdd("Websuche", "");
        SectionIcons.TryAdd("System-Prompt", "");
        SectionIcons.TryAdd("Fensterverhalten", "");
        var page = NewPage(ChatPage, null);

        // --- chat model (Mac PromptModelSelectionView „Chat Model“) ---
        page.Controls.Add(Section("Chat-Modell", first: true, subtitle: "Welches Modell im Chat antwortet. Schlüssel findest du unter Konten."));
        _chatEngine = EngineChoice(_settings.ChatEngine, "Chat", "Auf diesem PC (Sprachmodell)");
        _chatModel = ModelChoice(Core.GeminiModels.Prompt, _settings.ChatModel);
        _chatModelOpenAi = ModelChoice(Core.OpenAiModels.Prompt, _settings.ChatOpenAiModel);
        page.Controls.Add(Hidden(_chatEngine, _chatModel, _chatModelOpenAi));
        page.Controls.Add(ModelPicker("Chat-Modell", () => Core.ModelPickers.Prompt(LocalLlmName),
            models => Core.ModelPickers.Recommended(models, Core.Settings.DefaultPromptModel, Core.OpenAiModels.DefaultPromptModel),
            _chatEngine, _chatModel, _chatModelOpenAi, () => _llmChoice != null && OfflineEngine.LlmInstalled(SelectedLlmChoice)));

        // --- web search (Mac: Gemini searches by default; the Mac's own toggle is for Claude, which Windows does not offer) ---
        page.Controls.Add(Section("Websuche", subtitle: "Gemini kann im Web nach aktuellen Informationen suchen und zeigt seine Quellen unter der Antwort."));
        _chatWebSearch = new ToggleSwitch { Checked = _settings.ChatWebSearch, AccessibleName = "Gemini im Web suchen lassen" };
        var searchCard = new Card();
        searchCard.AddRow(SettingRow("Gemini im Web suchen lassen", "Google berechnet die Suche über deinen API-Schlüssel. Gesucht wird nur, "
            + "wenn eine Frage aktuelle Informationen braucht. Für OpenAI und das Modell auf diesem PC gibt es keine Websuche.",
            _chatWebSearch, ToggleWidth, out _));
        page.Controls.Add(searchCard);

        // --- system prompt ---
        page.Controls.Add(Section("System-Prompt", subtitle: "Anweisungen an das Modell im Chat. Bearbeite sie, um das Verhalten des Chats anzupassen."));
        _chatPrompt = new PromptEditor(Core.ChatPrompts.DefaultSystemPrompt, _settings.ChatSystemPrompt, "System-Prompt Chat");
        page.Controls.Add(PlainPromptCard(_chatPrompt));

        // --- how to use ---
        page.Controls.Add(Section("So geht's"));
        var keyShown = new ThemedLabel { AutoSize = true, Font = Theme.Strong, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        void ShowKey() => keyShown.Text = Hotkey.TryParse(_chatKey.Value, out var k) ? k.ToDisplayString() : "Nicht festgelegt";
        _chatKey.ValueChanged += ShowKey;
        ShowKey();
        var changeKey = new LinkLabel { Text = "Ändern …", AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "Kürzel für den Chat ändern" };
        changeKey.LinkClicked += (_, _) => { ShowPage(General); _chatKey.Focus(); };
        var keyLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        keyLine.Controls.Add(keyShown);
        keyLine.Controls.Add(changeKey);
        var howCard = new Card();
        howCard.AddRow(SettingRow("Tastenkürzel", null, keyLine, ComboWidth, out _));
        var how = Block();
        how.Controls.Add(Wrap("Öffne das Chatfenster mit dem Tastenkürzel oder dem Eintrag „Chat“ im Menü des Symbols unten rechts.", 2 * RowPadding));
        how.Controls.Add(Wrap("Tippe / im Chat, um die Befehle zu sehen. Meetings richtest du unter Einstellungen → Meetings ein.", 2 * RowPadding));
        how.Controls.Add(Wrap("Die Chats bleiben auf diesem PC gespeichert, links in der Liste. Im Offline-Modus antwortet das Sprachmodell "
            + "auf diesem PC, ohne Internet.", 2 * RowPadding, small: true));
        howCard.AddRow(how);
        page.Controls.Add(howCard);

        // --- Erweitert: window behaviour ---
        _chatCloseOnFocusLoss = new ToggleSwitch { Checked = _settings.ChatCloseOnFocusLoss, AccessibleName = "Chat schließen, wenn außerhalb geklickt wird" };
        var windowCard = new Card();
        windowCard.AddRow(SettingRow("Chat schließen, wenn außerhalb geklickt wird", "Schließt den Chat, sobald eine andere App im Vordergrund "
            + "ist. Mit /pin bleibt das Fenster offen.", _chatCloseOnFocusLoss, ToggleWidth, out _));
        AddAdvanced(page, Section("Fensterverhalten"), windowCard);
    }

    /// <summary>The page's state for <c>Snapshot()</c>.</summary>
    private string ChatSnapshot() => string.Join("\u0002", _chatKey.Value, SelectedEngine(_chatEngine), SelectedModel(_chatModel),
        SelectedModel(_chatModelOpenAi), _chatWebSearch.Checked, _chatCloseOnFocusLoss.Checked, _chatPrompt.Value);

    private sealed record ChatState(string Hotkey, bool HotkeyEnabled, Core.Engine Engine, string Model, string ModelOpenAi, bool WebSearch,
        bool CloseOnFocusLoss, string Prompt);

    /// <summary>Writes the page into the settings (from TrySave); returns what was there, for <see cref="RestoreChat"/>.</summary>
    /// <param name="keysOk">All shortcuts valid: only then the chat key is taken over, like the other keys.</param>
    private ChatState ApplyChat(bool keysOk)
    {
        var s = _settings;
        var before = new ChatState(s.ChatHotkey, s.ChatHotkeyEnabled, s.ChatEngine, s.ChatModel, s.ChatOpenAiModel, s.ChatWebSearch,
            s.ChatCloseOnFocusLoss, s.ChatSystemPrompt);
        if (keysOk) (s.ChatHotkey, s.ChatHotkeyEnabled) = StoredKey(_chatKey.Value, s.ChatHotkey);
        s.ChatEngine = SelectedEngine(_chatEngine);
        s.ChatModel = SelectedModel(_chatModel);
        s.ChatOpenAiModel = SelectedModel(_chatModelOpenAi);
        s.ChatWebSearch = _chatWebSearch.Checked;
        s.ChatCloseOnFocusLoss = _chatCloseOnFocusLoss.Checked;
        s.ChatSystemPrompt = _chatPrompt.Value;
        return before;
    }

    /// <summary>Saving failed: put the settings back as they were.</summary>
    private void RestoreChat(ChatState b)
    {
        var s = _settings;
        (s.ChatHotkey, s.ChatHotkeyEnabled, s.ChatEngine, s.ChatModel, s.ChatOpenAiModel) = (b.Hotkey, b.HotkeyEnabled, b.Engine, b.Model, b.ModelOpenAi);
        (s.ChatWebSearch, s.ChatCloseOnFocusLoss, s.ChatSystemPrompt) = (b.WebSearch, b.CloseOnFocusLoss, b.Prompt);
    }

    /// <summary>The chat key: valid and not taken by a key checked before it; then it is taken, too.</summary>
    private string? ChatKeyProblem(List<(string Owner, Hotkey Key)> taken, string rule)
    {
        if (_chatKey.Value.Length == 0) return null;
        if (!Hotkey.TryParse(_chatKey.Value, out var key) || !IsSafe(key)) return "Kürzel für den Chat: " + rule;
        if (taken.FirstOrDefault(k => k.Key == key) is { Owner: { } owner })
            return $"{key.ToDisplayString()} ist schon für „{owner}“ vergeben. Bitte für den Chat ein anderes Kürzel wählen.";
        taken.Add(("Chat", key));
        return null;
    }
}
