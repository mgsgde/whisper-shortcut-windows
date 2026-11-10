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

    private ModelStepControls _meetingSpeechStep = null!, _meetingTextStep = null!;
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
        _meetingSpeechStep = new ModelStepControls(Core.ModelStep.Transcription, _settings.MeetingTranscription with { Engine = _meetingSpeechEngineShown },
            "Meeting-Transkription", "Auf diesem PC (Parakeet)");
        page.Controls.Add(_meetingSpeechStep.Hide());
        page.Controls.Add(ModelPicker("Modell für Meeting-Transkription", _meetingSpeechStep));

        // --- summary model ---
        page.Controls.Add(Section("Modell für Meeting-Zusammenfassungen", subtitle: "Modell für die Zusammenfassung am Ende des Meetings."));
        _meetingTextStep = new ModelStepControls(Core.ModelStep.Text, _settings.MeetingText, "Meeting-Zusammenfassung", "Auf diesem PC (Sprachmodell)");
        page.Controls.Add(_meetingTextStep.Hide());
        page.Controls.Add(ModelPicker("Modell für Meeting-Zusammenfassungen", _meetingTextStep, Core.MeetingOptions.DefaultSummaryModel));

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
    private string MeetingsSnapshot() => string.Join("\u0002", _meetingSpeechStep.Snapshot, _meetingTextStep.Snapshot,
        SelectedChoice(_meetingChunk), SelectedChoice(_meetingSafeguard));

    // The engine the page showed when it was built; a different pick on save makes the choice explicit (else meetings follow Diktieren).
    private Core.Engine _meetingSpeechEngineShown;

    private sealed record MeetingsState(Core.ModelSelection Speech, bool SpeechExplicit, Core.ModelSelection Text, int Chunk, int Safeguard);

    /// <summary>Writes the page into the settings (from TrySave); returns what was there, for <see cref="RestoreMeetings"/>.</summary>
    private MeetingsState ApplyMeetings()
    {
        var s = _settings;
        var before = new MeetingsState(s.MeetingTranscription, s.MeetingSpeechEngineExplicit, s.MeetingText, s.MeetingChunkSeconds, s.MeetingSafeguardMinutes);
        var pickedSpeech = _meetingSpeechStep.Selected;
        if (pickedSpeech.Engine != _meetingSpeechEngineShown) s.MeetingSpeechEngineExplicit = true;
        // Until the engine was picked, the stored engine stays (meetings follow Diktieren); the models are always stored.
        s.MeetingTranscription = s.MeetingSpeechEngineExplicit ? pickedSpeech : pickedSpeech with { Engine = s.MeetingTranscription.Engine };
        s.MeetingText = _meetingTextStep.Selected;
        s.MeetingChunkSeconds = SelectedChoice(_meetingChunk);
        s.MeetingSafeguardMinutes = SelectedChoice(_meetingSafeguard);
        return before;
    }

    /// <summary>Saving failed: put the settings back as they were.</summary>
    private void RestoreMeetings(MeetingsState b)
    {
        var s = _settings;
        (s.MeetingTranscription, s.MeetingSpeechEngineExplicit, s.MeetingText) = (b.Speech, b.SpeechExplicit, b.Text);
        (s.MeetingChunkSeconds, s.MeetingSafeguardMinutes) = (b.Chunk, b.Safeguard);
    }
}
