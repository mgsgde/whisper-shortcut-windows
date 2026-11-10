using System.Diagnostics;

namespace WhisperShortcut;

/// <summary>
/// Page „Meetings“ (Mac MeetingsSettingsTab: how it works with the transcripts folder, the transcription model, the summary
/// model, the pointer to Claude &amp; KI-Apps, and under „Erweitert“ the chunk interval and the meeting safeguard). Own file:
/// the main settings file only gets the hook lines (nav entry, <see cref="BuildMeetingsPage"/>, <see cref="MeetingsSnapshot"/>,
/// <see cref="ApplyMeetings"/> / <see cref="RestoreMeetings"/>).
/// </summary>
internal sealed partial class SettingsForm
{
    public const string MeetingsPage = "Meetings";

    private FluentCombo _meetingSpeechEngine = null!, _meetingModel = null!, _meetingModelOpenAi = null!;
    private FluentCombo _meetingTextEngine = null!, _meetingSummaryModel = null!, _meetingSummaryModelOpenAi = null!;
    private FluentCombo _meetingChunk = null!, _meetingSafeguard = null!;

    private sealed record ChoiceItem(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    private void BuildMeetingsPage()
    {
        SectionIcons.TryAdd("Meetings", "");
        SectionIcons.TryAdd("Modell für Meeting-Transkription", "");
        SectionIcons.TryAdd("Modell für Meeting-Zusammenfassungen", "");
        SectionIcons.TryAdd("Meetings in Claude", "");
        SectionIcons.TryAdd("Abschnittslänge", "");
        SectionIcons.TryAdd("Meeting-Schutz", "");
        var page = NewPage(MeetingsPage, null);

        // --- how it works (Mac: started from the chat; Windows also from the tray menu and its own shortcut) ---
        page.Controls.Add(Section("Meetings", first: true, subtitle: "Nimm ein Meeting auf: im Chat mit /meeting oder über das Symbol unten "
            + "rechts → Meeting → Meeting starten. Das Transkript entsteht, während ihr sprecht, wird am Ende zusammengefasst, und du kannst "
            + "den Chat jederzeit dazu fragen."));
        var openFolder = new FluentButton("Transkriptordner öffnen") { AutoSize = true, MinimumSize = new Size(ButtonWidth, 32), Margin = new Padding(0, 0, 0, 4) };
        openFolder.Click += (_, _) => OpenMeetingsFolder();
        page.Controls.Add(openFolder);

        // --- transcription model ---
        page.Controls.Add(Section("Modell für Meeting-Transkription", subtitle: "Welches Modell die Meeting-Aufnahme in Text verwandelt. "
            + "Glossar und Anweisungen kommen von Diktieren."));
        _meetingSpeechEngineShown = _settings.ConfiguredMeetingSpeechEngine;
        _meetingSpeechEngine = EngineChoice(_meetingSpeechEngineShown, "Meeting-Transkription", "Auf diesem PC (Parakeet)");
        _meetingModel = ModelChoice(Core.GeminiModels.Transcription, _settings.MeetingTranscriptionModel);
        _meetingModelOpenAi = ModelChoice(Core.OpenAiModels.Transcription, _settings.MeetingOpenAiTranscriptionModel);
        page.Controls.Add(Hidden(_meetingSpeechEngine, _meetingModel, _meetingModelOpenAi));
        page.Controls.Add(ModelPicker("Modell für Meeting-Transkription", () => Core.ModelPickers.Transcription("Parakeet"),
            models => Core.ModelPickers.Recommended(models, Core.Settings.DefaultTranscriptionModel, Core.OpenAiModels.DefaultTranscriptionModel),
            _meetingSpeechEngine, _meetingModel, _meetingModelOpenAi, () => OfflineEngine.SpeechInstalled));

        // --- summary model ---
        page.Controls.Add(Section("Modell für Meeting-Zusammenfassungen", subtitle: "Modell für die Zusammenfassung am Ende des Meetings."));
        _meetingTextEngine = EngineChoice(_settings.MeetingTextEngine, "Meeting-Zusammenfassung", "Auf diesem PC (Sprachmodell)");
        _meetingSummaryModel = ModelChoice(Core.GeminiModels.Prompt, _settings.MeetingSummaryModel);
        _meetingSummaryModelOpenAi = ModelChoice(Core.OpenAiModels.Prompt, _settings.MeetingOpenAiSummaryModel);
        page.Controls.Add(Hidden(_meetingTextEngine, _meetingSummaryModel, _meetingSummaryModelOpenAi));
        page.Controls.Add(ModelPicker("Modell für Meeting-Zusammenfassungen", () => Core.ModelPickers.Prompt(LocalLlmName),
            models => Core.ModelPickers.Recommended(models, Core.MeetingOptions.DefaultSummaryModel, Core.OpenAiModels.DefaultPromptModel),
            _meetingTextEngine, _meetingSummaryModel, _meetingSummaryModelOpenAi,
            () => _llmChoice != null && OfflineEngine.LlmInstalled(SelectedLlmChoice)));

        // --- Claude & KI-Apps pointer ---
        page.Controls.Add(Section("Meetings in Claude", subtitle: "Claude und andere KI-Apps auf diesem PC können deine Meetings lesen, auch "
            + "das, das gerade aufgenommen wird. Verbinde sie unter Claude & KI-Apps."));
        var openAiApps = new FluentButton("Claude & KI-Apps öffnen") { AutoSize = true, MinimumSize = new Size(ButtonWidth, 32), Margin = new Padding(0, 0, 0, 4) };
        openAiApps.Click += (_, _) => ShowPage(AiAppsPage);
        page.Controls.Add(openAiApps);

        // --- Erweitert: chunk interval and safeguard ---
        _meetingChunk = new FluentCombo { Width = ComboWidth, AccessibleName = "Abschnittslänge bei Meetings" };
        foreach (var sec in Core.MeetingOptions.ChunkSeconds) _meetingChunk.Items.Add(new ChoiceItem(sec, Core.MeetingOptions.ChunkLabel(sec)));
        SelectChoice(_meetingChunk, _settings.MeetingChunkSeconds);
        var chunkCard = new Card();
        chunkCard.AddRow(SettingRow("Intervall", "Kürzere Intervalle aktualisieren schneller, brauchen aber mehr API-Aufrufe.",
            _meetingChunk, ComboWidth, out _));

        _meetingSafeguard = new FluentCombo { Width = ComboWidth, AccessibleName = "Nachfragen, wenn ein Meeting länger dauert als" };
        foreach (var min in Core.MeetingOptions.SafeguardMinutes) _meetingSafeguard.Items.Add(new ChoiceItem(min, Core.MeetingOptions.SafeguardLabel(min)));
        SelectChoice(_meetingSafeguard, _settings.MeetingSafeguardMinutes);
        var safeguardCard = new Card();
        safeguardCard.AddRow(SettingRow("Nachfragen, wenn ein Meeting länger dauert als", "Mit „Nie“ ausschalten.", _meetingSafeguard, ComboWidth, out _));

        AddAdvanced(page,
            Section("Abschnittslänge", subtitle: "Wie oft die Aufnahme transkribiert und an die Datei angehängt wird."), chunkCard,
            Section("Meeting-Schutz", subtitle: "Nach dieser Dauer nachfragen, ob das Meeting beendet oder weiter transkribiert werden soll."),
            safeguardCard);
    }

    private static void SelectChoice(FluentCombo combo, int value) =>
        combo.SelectedItem = combo.Items.Cast<ChoiceItem>().FirstOrDefault(i => i.Value == value) ?? combo.Items[^1];

    private static int SelectedChoice(FluentCombo combo) => ((ChoiceItem)combo.SelectedItem!).Value;

    private static void OpenMeetingsFolder()
    {
        try
        {
            Directory.CreateDirectory(Core.MeetingStore.DefaultRoot);
            Process.Start(new ProcessStartInfo { FileName = Core.MeetingStore.DefaultRoot, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Core.Log.Error("Meetings folder could not be opened", e);
        }
    }

    /// <summary>The page's state for <c>Snapshot()</c>.</summary>
    private string MeetingsSnapshot() => string.Join("\u0002", SelectedEngine(_meetingSpeechEngine), SelectedModel(_meetingModel),
        SelectedModel(_meetingModelOpenAi), SelectedEngine(_meetingTextEngine), SelectedModel(_meetingSummaryModel),
        SelectedModel(_meetingSummaryModelOpenAi), SelectedChoice(_meetingChunk), SelectedChoice(_meetingSafeguard));

    // The engine the page showed when it was built; a different pick on save makes the choice explicit (else meetings follow Diktieren).
    private Core.Engine _meetingSpeechEngineShown;

    private sealed record MeetingsState(Core.Engine Speech, bool SpeechExplicit, string Model, string ModelOpenAi, Core.Engine Text, string Summary,
        string SummaryOpenAi, int Chunk, int Safeguard);

    /// <summary>Writes the page into the settings (from TrySave); returns what was there, for <see cref="RestoreMeetings"/>.</summary>
    private MeetingsState ApplyMeetings()
    {
        var s = _settings;
        var before = new MeetingsState(s.MeetingSpeechEngine, s.MeetingSpeechEngineExplicit, s.MeetingTranscriptionModel, s.MeetingOpenAiTranscriptionModel,
            s.MeetingTextEngine, s.MeetingSummaryModel, s.MeetingOpenAiSummaryModel, s.MeetingChunkSeconds, s.MeetingSafeguardMinutes);
        var pickedSpeech = SelectedEngine(_meetingSpeechEngine);
        if (pickedSpeech != _meetingSpeechEngineShown) s.MeetingSpeechEngineExplicit = true;
        if (s.MeetingSpeechEngineExplicit) s.MeetingSpeechEngine = pickedSpeech;
        s.MeetingTranscriptionModel = SelectedModel(_meetingModel);
        s.MeetingOpenAiTranscriptionModel = SelectedModel(_meetingModelOpenAi);
        s.MeetingTextEngine = SelectedEngine(_meetingTextEngine);
        s.MeetingSummaryModel = SelectedModel(_meetingSummaryModel);
        s.MeetingOpenAiSummaryModel = SelectedModel(_meetingSummaryModelOpenAi);
        s.MeetingChunkSeconds = SelectedChoice(_meetingChunk);
        s.MeetingSafeguardMinutes = SelectedChoice(_meetingSafeguard);
        return before;
    }

    /// <summary>Saving failed: put the settings back as they were.</summary>
    private void RestoreMeetings(MeetingsState b)
    {
        var s = _settings;
        (s.MeetingSpeechEngine, s.MeetingSpeechEngineExplicit) = (b.Speech, b.SpeechExplicit);
        (s.MeetingTranscriptionModel, s.MeetingOpenAiTranscriptionModel) = (b.Model, b.ModelOpenAi);
        (s.MeetingTextEngine, s.MeetingSummaryModel, s.MeetingOpenAiSummaryModel) = (b.Text, b.Summary, b.SummaryOpenAi);
        (s.MeetingChunkSeconds, s.MeetingSafeguardMinutes) = (b.Chunk, b.Safeguard);
    }
}
